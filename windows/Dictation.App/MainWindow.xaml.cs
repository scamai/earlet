using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dictation.Core;
using Dictation.Core.Learning;
using Microsoft.Extensions.Logging;

namespace Dictation.App;

/// <summary>
/// The window: renders <see cref="DictationViewState"/> and
/// <see cref="VocabularyViewState"/> and forwards clicks. Behaviour lives in
/// <see cref="DictationController"/>, including whether a close hides the
/// window or exits, and in <see cref="VocabularyModel"/>.
/// </summary>
public partial class MainWindow : Window, IDictationView, IVocabularyView, IVocabularyTeachRow
{
    /// <summary>Calls <see cref="DictationController.Tick"/>: every 30 ms
    /// while there is work for it, once a second while dictation is off
    /// (<see cref="PacePoll"/>).</summary>
    private readonly DispatcherTimer poll = new() { Interval = DictationController.BusyPoll };

    /// <summary>Brings the status line back after a Check again that found
    /// nothing new (<see cref="MicrophoneRecheckClick"/>).</summary>
    private readonly DispatcherTimer recheckPause = new() { Interval = LiveRegionAnnouncement.RepeatPause };

    private readonly DictationController controller;
    private readonly VocabularyModel vocabulary;
    private readonly DictationTray tray;
    private readonly DictationHudWindow? hud;
    private readonly PracticeDisclosure practice = new();

    private bool rendering;
    private bool quitting;
    private bool released;

    /// <summary>The hold is listening: <see cref="SetLiveText"/> raises
    /// nothing until it stops (<see cref="DictationViewState.MicrophoneOpen"/>).</summary>
    private bool microphoneOpen;

    /// <summary>The sentence a Check again got back unchanged, while the
    /// status line is out of sight for <see cref="recheckPause"/>.</summary>
    private string? recheckRepeated;

    /// <summary>The rows the vocabulary list was last built from. A render
    /// that changes only a line above it — a correction notice, two or three
    /// per dictation — keeps the row buttons, and keyboard focus on one.</summary>
    private IReadOnlyList<VocabularyRow>? listedRows;

    /// <summary>Save again had the keyboard focus when it was pressed (a
    /// click gives it the focus too). Disabling it while it saves takes the
    /// focus off it; the render that ends the save gives it back
    /// (<see cref="RenderSaveAgain"/>).</summary>
    private bool saveAgainHadKeyboard;

    /// <param name="parts">What the window is built over: null for the
    /// user's own (<see cref="DictationWindowParts.ForUser"/>), or the render
    /// mode's inert ones (<see cref="DictationRenderMode"/>).</param>
    public MainWindow(ILoggerFactory logs, bool atLogin, DictationWindowParts? parts = null)
    {
        InitializeComponent();
        Footer.Text = DictationCopy.Footer;
        ShowVocabularyLabels();
        ShowPracticeLabels();
        ShowRecentLabels();
        ShowSavedTakeLabels();
        ShowHeaderAndTroubleshootingLabels();

        parts ??= DictationWindowParts.ForUser(logs);
        var learning = parts.Learning;
        tray = new DictationTray(logs.CreateLogger<DictationTray>(), parts.TrayIcon);
        try
        {
            // The floating indicator: what a hold is doing and how it ended,
            // near the user's work while this window is hidden in the
            // notification area. None in a render, which never shows a pill.
            if (parts.Indicator)
            {
                hud = new DictationHudWindow(logs.CreateLogger<DictationHudWindow>());
            }

            controller = new DictationController(
                parts.Platform,
                this,
                action => Dispatcher.InvokeAsync(action),
                logs,
                parts.Settings,
                parts.Environment,
                parts.AppDirectory,
                parts.Options,
                learning: learning,
                hud: hud);
            vocabulary = new VocabularyModel(
                learning, this, action => Dispatcher.InvokeAsync(action), parts.Corrections(controller.Corrections),
                // learning.json is written and flushed off this thread, where
                // the keyboard hook runs, one change at a time in click order.
                new SerialWorkQueue().Run, logs.CreateLogger<VocabularyModel>());
            // Spoken by the indicator only from behind: in front, the
            // section's own live line is what Narrator reads (SetLiveText).
            vocabulary.CorrectionRemembered += (_, _) => controller.ShowCorrectionRemembered(announce: !IsActive);
            // The menu's items make the same calls as the window's buttons.
            var trayCommands = new DictationTrayCommands(controller, vocabulary, ShowFromOutside, ReviewVocabulary, Quit);
            tray.Chosen += async (_, item) => await trayCommands.ChooseAsync(item);
            HoldKeyPicker.ItemsSource = controller.HoldKeyChoices;
            MouseButtonPicker.ItemsSource = controller.MouseButtonChoices;
            LanguagePicker.ItemsSource = controller.LanguageChoices;
            SilenceStopPicker.ItemsSource = controller.SilenceStopChoices;
            poll.Tick += (_, _) =>
            {
                controller.Tick();
                PacePoll();
            };
            recheckPause.Tick += (_, _) => EndRecheckPause();
            // Coming back to the window is how the user returns from Windows'
            // privacy page: ask again whether the microphone is still blocked.
            Activated += (_, _) => controller.RecheckMicrophone();
            // The user can switch the sign-in entry off in Settings or Task
            // Manager while Dictation runs: read it again. The Mac reads only
            // when its window appears; this read keeps a refusal's note while
            // the box is where the refusal left it (RefreshLaunchAtLogin).
            Activated += (_, _) => controller.RefreshLaunchAtLogin();
            Closing += OnClosing;
            Closed += (_, _) =>
            {
                try
                {
                    LetGo();
                }
                finally
                {
                    // Whatever failed above, the process ends. App holds the
                    // single-instance mutex until it exits, so a process left
                    // running with no window would keep it, and every later
                    // launch would hand over to a process with nothing to show.
                    Application.Current.Shutdown();
                }
            };
            Startup = controller.Start(atLogin);
            vocabulary.Refresh();
            poll.Start();
        }
        catch
        {
            // App reports the failure and exits, and Closed never runs for a
            // window that was never finished. Without this the icon stays
            // drawn in the notification area after the process has gone, until
            // the mouse passes over it.
            try
            {
                LetGo();
            }
            catch (Exception cleanup)
            {
                // The failure to report is the one that stopped the window.
                logs.CreateLogger<MainWindow>().LogWarning(cleanup, "Letting go after a failed start failed too");
            }

            throw;
        }
    }

    /// <summary>How the app started: whether the window is shown at all.</summary>
    public DictationStartup Startup { get; }

    /// <summary>
    /// Let go of what the constructor made: the timer, the controller, the
    /// indicator, the vocabulary and the notification-area icon, in that order.
    /// </summary>
    /// <remarks>
    /// The controller goes before the indicator: disposing it takes the pill
    /// down through the window it is about to lose. Every step runs even when
    /// one before it throws, and the first failure is thrown again once they
    /// all have. The icon matters most: Windows keeps drawing an icon whose
    /// process has gone until the mouse passes over it. A field is null here
    /// only when the constructor failed before it was made.
    /// </remarks>
    private void LetGo()
    {
        // Once only. A window whose constructor failed after wiring Closed is
        // still one WPF closes at shutdown, and that would come back here.
        if (released)
        {
            return;
        }

        released = true;
        ExceptionDispatchInfo? first = null;
        void Step(Action step)
        {
            try
            {
                step();
            }
            catch (Exception error)
            {
                first ??= ExceptionDispatchInfo.Capture(error);
            }
        }

        Step(poll.Stop);
        Step(recheckPause.Stop);
        Step(() => controller?.Dispose());
        Step(() => hud?.Close());
        Step(() => vocabulary?.Dispose());
        Step(tray.Dispose);
        first?.Throw();
    }

    public void Render(DictationViewState state)
    {
        // First: every live text below is quiet while the microphone is open.
        microphoneOpen = state.MicrophoneOpen;
        rendering = true;
        try
        {
            HoldKeyPicker.SelectedItem = state.HoldKey;
            MouseButtonPicker.SelectedItem =
                Dictation.Services.DictationMouseButtons.Choices.First(c => c.Value == state.MouseButton);
            LanguagePicker.SelectedItem = state.Language;
            SilenceStopPicker.SelectedItem = DictationSilenceStopChoice.For(state.SilenceStop);
            LaunchAtLoginBox.IsChecked = state.LaunchAtLogin;
            LearnBox.IsChecked = state.LearnFromCorrections;
        }
        finally
        {
            rendering = false;
        }

        HoldKeyPicker.IsEnabled = state.CanChooseHoldKey;
        MouseButtonPicker.IsEnabled = state.CanChooseMouseButton;
        LanguagePicker.IsEnabled = state.CanChooseLanguage;
        // Shown before the text changes, so the announcement comes from an
        // element a reader can see.
        HoldKeyNotice.Visibility = state.HoldKeyNotice is null ? Visibility.Collapsed : Visibility.Visible;
        SetLiveText(HoldKeyNotice, state.HoldKeyNotice ?? string.Empty);
        MouseButtonNotice.Visibility = state.MouseButtonNotice is null ? Visibility.Collapsed : Visibility.Visible;
        SetLiveText(MouseButtonNotice, state.MouseButtonNotice ?? string.Empty);
        Instructions.Text = state.Instructions;
        SetLiveText(Status, state.Status);
        // Asked before the actions and Cancel can hide: a hidden button has no focus.
        var actionsGoing = !state.OffersMicrophoneSettings && MicrophoneActions.IsKeyboardFocusWithin;
        var cancelGoing = !state.CanCancel && CancelButton.IsKeyboardFocusWithin;
        RenderHeader(state.Header);
        // Cancel dictation shows only while there is something to cancel, as
        // the Mac header's does.
        CancelButton.IsEnabled = state.CanCancel;
        CancelButton.Visibility = state.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        if (actionsGoing || cancelGoing)
        {
            // Check again worked, or the dictation Cancel was for is over, and
            // the button is about to go or has gone: keep the keyboard in the
            // window rather than on nothing, on a button this render left live.
            FocusRestingButton();
        }

        MicrophoneActions.Visibility =
            state.OffersMicrophoneSettings ? Visibility.Visible : Visibility.Collapsed;
        RenderRecent(state.Recent);
        RenderSavedTake(state.SavedTake);
        RenderPractice(state);
        // Shown before the text changes, so the announcement comes from an
        // element a reader can see.
        LaunchAtLoginNote.Visibility = state.LaunchAtLoginNote is null ? Visibility.Collapsed : Visibility.Visible;
        SetLiveText(LaunchAtLoginNote, state.LaunchAtLoginNote ?? string.Empty);
        tray.Update(state.Tray);
        // The switch is the controller's; the correction notice follows it.
        vocabulary?.ApplyLearningSwitch(state.LearnFromCorrections);
        PacePoll();
    }

    /// <summary>
    /// Set the poll to what the controller wants now
    /// (<see cref="DictationController.PollInterval"/>): an Enable or a Copy
    /// last text repaints, so the poll is fast again before the next tick
    /// would have come. Only on a change: setting the interval restarts the
    /// timer. Null while the controller's own constructor renders.
    /// </summary>
    private void PacePoll()
    {
        if (controller?.PollInterval is { } wanted && poll.Interval != wanted)
        {
            poll.Interval = wanted;
        }
    }

    /// <summary>
    /// Where the keyboard goes when the control it is on goes: Enable/Pause,
    /// when it is live; during a dictation, when it is not (Pause is inactive
    /// while one listens, transcribes or types), Cancel dictation beside it;
    /// and while the words are typed, when Cancel has gone too, the
    /// Troubleshooting section at the bottom of the window, which is always
    /// there and changes nothing if a key lands on it. Focus() on a disabled
    /// or collapsed button does nothing, which would leave the keyboard on
    /// nothing. Read from the buttons as the latest render left them, so call
    /// it after those are set. Where WPF puts the keyboard inside the
    /// Expander has never been seen.
    /// </summary>
    private void FocusRestingButton() =>
        (new UIElement[] { EnablePauseButton, CancelButton }
            .FirstOrDefault(button => button.IsEnabled && button.Visibility == Visibility.Visible)
            ?? TroubleshootingSection)
            .Focus();

    public void Render(VocabularyViewState state)
    {
        RenderLearningReceipt(state);
        tray.UpdateLearning(state.CanUndoLearning);
        VocabularyEmpty.Text = state.EmptyText ?? string.Empty;
        VocabularyEmpty.Visibility = state.EmptyText is null ? Visibility.Collapsed : Visibility.Visible;
        CorrectionNotice.Visibility = state.CorrectionNotice is null ? Visibility.Collapsed : Visibility.Visible;
        SetLiveText(CorrectionNotice, state.CorrectionNotice ?? string.Empty);
        TeachMessage.Visibility = state.TeachMessage is null ? Visibility.Collapsed : Visibility.Visible;
        SetLiveText(TeachMessage, state.TeachMessage ?? string.Empty);
        VocabularySaveReceipt.Visibility = state.ManagementNotice is null ? Visibility.Collapsed : Visibility.Visible;
        SetLiveText(VocabularySaveReceipt, state.ManagementNotice ?? string.Empty);
        VocabularyNotSaved.Visibility = state.NotSavedText is null ? Visibility.Collapsed : Visibility.Visible;
        SetLiveText(VocabularyNotSaved, state.NotSavedText ?? string.Empty);
        RenderSaveAgain(state);
        ForgetAllButton.IsEnabled = state.CanForgetAll;
        ForgetAllConfirmation.Visibility = state.ConfirmingForgetAll ? Visibility.Visible : Visibility.Collapsed;

        if (listedRows is null || !listedRows.SequenceEqual(state.Rows))
        {
            listedRows = state.Rows;
            VocabularyList.Children.Clear();
            foreach (var row in state.Rows)
            {
                VocabularyList.Children.Add(RowFor(row));
            }
        }
    }

    /// <summary>
    /// Put <paramref name="text"/> in one of this window's polite live regions
    /// and, when it is new and the user is in this window, have Narrator say it.
    /// </summary>
    /// <remarks>
    /// <c>LiveSetting="Polite"</c> in the markup only marks the element; WPF
    /// announces it when <c>LiveRegionChanged</c> is raised, and before this
    /// nothing raised it. Only while this window is active: during a hold the
    /// user is in another app and the floating indicator says the same thing
    /// there, and two polite announcements of one ending would be read one
    /// after the other. And never while the hold is listening, even in this
    /// window (into its practice field, say): the microphone is open, and
    /// Narrator through the speakers would be dictated with the user's words.
    /// The text still changes; the release announces what follows.
    /// </remarks>
    private void SetLiveText(TextBlock block, string text)
    {
        var previous = block.Text;
        block.Text = text;
        if (IsActive && !microphoneOpen && LiveRegionAnnouncement.ShouldRaise(previous, text))
        {
            LiveRegion.Raise(block);
        }
    }

    /// <summary>Show the window and bring it forward: the notification-area
    /// icon, a second launch, or a failure while it was hidden.</summary>
    public void ShowFromOutside()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        // Windows may refuse the foreground to a window that was not clicked;
        // a momentary Topmost puts it above the others either way.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Windows is signing out or shutting down: close for real.</summary>
    public void PrepareForSessionEnd() => quitting = true;

    void IDictationView.Quit() => Quit();

    void IDictationView.Reveal() => ShowFromOutside();

    private void Quit()
    {
        quitting = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var decision = controller.RequestClose(quitting);
        switch (decision.Action)
        {
            case DictationCloseAction.Hide:
                e.Cancel = true;
                Hide();
                if (decision.Notice is { } notice)
                {
                    tray.ShowNotice(notice);
                }

                break;
            case DictationCloseAction.Wait:
                e.Cancel = true;
                break;
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e) => controller.Cancel();

    private void HoldKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && HoldKeyPicker.SelectedItem is Dictation.Speech.HotkeyBinding key)
        {
            controller.ChooseHoldKey(key.VirtualKey);
        }
    }

    private void MouseButtonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && MouseButtonPicker.SelectedItem is Dictation.Services.DictationMouseButtons.Choice choice)
        {
            controller.ChooseMouseButton(choice.Value);
        }
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && LanguagePicker.SelectedItem is SpeechLanguage language)
        {
            controller.ChooseLanguage(language.Code);
        }
    }

    private void SilenceStopChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!rendering && SilenceStopPicker.SelectedItem is DictationSilenceStopChoice choice)
        {
            controller.ChooseSilenceStop(choice.Value);
        }
    }

    private void LaunchAtLoginClick(object sender, RoutedEventArgs e) =>
        controller.SetLaunchAtLogin(LaunchAtLoginBox.IsChecked == true);

    private void OpenLogFolderClick(object sender, RoutedEventArgs e) => controller.OpenLogFolder();

    // ─── Vocabulary ─────────────────────────────────────────────────────

    /// <summary>The section's fixed text, from <see cref="DictationCopy"/>
    /// like every other sentence; the access keys are this window's.</summary>
    private void ShowVocabularyLabels()
    {
        VocabularyHeading.Text = DictationCopy.VocabularyHeading;
        LearnBox.Content = WithAccessKey(DictationCopy.LearnFromCorrections, "r");
        LearnDisclosure.Text = DictationCopy.LearnFromCorrectionsDisclosure;
        TeachWordHint.Text = DictationCopy.TeachWordPlaceholder;
        AutomationProperties.SetName(TeachWord, DictationCopy.TeachWordPlaceholder);
        TeachSoundsLikeHint.Text = DictationCopy.TeachSoundsLikePlaceholder;
        AutomationProperties.SetName(TeachSoundsLike, DictationCopy.TeachSoundsLikePlaceholder);
        AutomationProperties.SetHelpText(TeachSoundsLike, DictationCopy.TeachSoundsLikeHelp);
        TeachSoundsLikeHelp.Text = DictationCopy.TeachSoundsLikeHelp;
        TeachButton.Content = WithAccessKey(DictationCopy.TeachAdd, "A");
        ForgetAllButton.Content = WithAccessKey(DictationCopy.ForgetAll, "g");
        ForgetAllQuestion.Text = DictationCopy.ForgetAllQuestion;
        ConfirmForgetAllButton.Content = DictationCopy.ForgetAll;
        CancelForgetAllButton.Content = DictationCopy.ForgetAllCancel;
        LearningReceiptHeading.Text = DictationCopy.LearningReceiptHeading;
        UndoLearningButton.Content = WithAccessKey(DictationCopy.UndoLearning, "U");
        DismissLearningButton.Content = DictationCopy.DismissLearningReceipt;
        AutomationProperties.SetName(DismissLearningButton, DictationCopy.DismissLearningReceiptName);
    }

    /// <summary>
    /// "Learning from you": shown while there is a receipt, with Undo learning
    /// while it is offered. A button about to go hands the focus on first —
    /// Undo to Dismiss, the section to the resting button
    /// (<see cref="FocusRestingButton"/>) — so it never lands on nothing.
    /// </summary>
    /// <remarks>
    /// Decided on the window's logical focus, not the keyboard's: an outcome
    /// usually changes the section while the user is fixing text in another
    /// app, when no element here has keyboard focus, and on activation WPF
    /// would hand the keyboard back to a button that has gone. Focus() in an
    /// inactive window moves the logical focus only.
    /// </remarks>
    private void RenderLearningReceipt(VocabularyViewState state)
    {
        var shown = state.LearningReceipt is not null;
        var focused = FocusManager.GetFocusedElement(this);
        if (shown && !state.CanUndoLearning && ReferenceEquals(focused, UndoLearningButton))
        {
            DismissLearningButton.Focus();
        }
        else if (!shown && (ReferenceEquals(focused, UndoLearningButton) || ReferenceEquals(focused, DismissLearningButton)))
        {
            FocusRestingButton();
        }

        UndoLearningButton.Visibility = state.CanUndoLearning ? Visibility.Visible : Visibility.Collapsed;
        // Shown before the text changes, so the announcement comes from an
        // element a reader can see.
        LearningReceiptSection.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        SetLiveText(LearningReceipt, state.LearningReceipt ?? string.Empty);
    }

    private void UndoLearningClick(object sender, RoutedEventArgs e) => vocabulary.UndoLastLearning();

    private void DismissLearningClick(object sender, RoutedEventArgs e) => vocabulary.DismissLearningReceipt();

    /// <summary>The label with an access key on the first <paramref name="key"/>.</summary>
    private static string WithAccessKey(string label, string key)
    {
        var at = label.IndexOf(key, StringComparison.Ordinal);
        return at < 0 ? label : label.Insert(at, "_");
    }

    private UIElement RowFor(VocabularyRow row)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.Text, TextWrapping = TextWrapping.Wrap });
        if (row.Detail is { } detail)
        {
            var secondary = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap };
            secondary.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);
            text.Children.Add(secondary);
        }

        var remove = new Button
        {
            Content = DictationCopy.RemoveEntry,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(remove, $"{DictationCopy.RemoveEntry} {row.Text}");
        remove.Click += (_, _) => vocabulary.Remove(row);

        var line = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(remove, 1);
        line.Children.Add(text);
        line.Children.Add(remove);
        return line;
    }

    // ─── Try dictation ──────────────────────────────────────────────────

    /// <summary>The practice section's fixed text, from <see cref="DictationCopy"/>.</summary>
    private void ShowPracticeLabels()
    {
        PracticeSection.Header = WithAccessKey(DictationCopy.PracticeHeading, "T");
        AutomationProperties.SetName(PracticeSection, DictationCopy.PracticeHeading);
        PracticeHint.Text = DictationCopy.PracticePlaceholder;
        AutomationProperties.SetName(PracticeBox, DictationCopy.PracticeFieldName);
        PracticeNotSaved.Text = DictationCopy.PracticeNotSaved;
    }

    /// <summary>
    /// Show or hide "Try dictation", and open or close it when
    /// <see cref="PracticeDisclosure"/> says to: open when it appears before
    /// any dictation has landed, closed after the first one, unless the
    /// practice field has the focus.
    /// </summary>
    /// <remarks>
    /// The field's focus is the keyboard's, or, while the window is inactive,
    /// the window's own: the element activation gives the keyboard back to. A
    /// first dictation into another app must not collapse the section under
    /// that, or the keyboard would come back to a hidden field. When the
    /// section hides with the keyboard in it, the keyboard moves to the
    /// resting button, as it does when the microphone actions go, and the practice
    /// text goes with the section, as the Mac's does.
    /// </remarks>
    private void RenderPractice(DictationViewState state)
    {
        PracticeInstructions.Text = DictationCopy.PracticeInstructions(state.HoldKey);
        // Asked before the section can hide: a hidden field has no focus.
        var keyboard = PracticeBox.IsKeyboardFocusWithin;
        var focused = keyboard || ReferenceEquals(FocusManager.GetFocusedElement(this), PracticeBox);
        if (practice.Next(state.PracticeVisible, state.PracticeStartsExpanded, focused) is { } expanded)
        {
            PracticeSection.IsExpanded = expanded;
        }

        if (!state.PracticeVisible && PracticeSection.Visibility == Visibility.Visible)
        {
            if (keyboard)
            {
                FocusRestingButton();
            }

            PracticeBox.Clear();
        }

        PracticeSection.Visibility = state.PracticeVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The placeholder shows only over an empty field. Its length is
    /// all this window ever reads of the practice text.</summary>
    private void PracticeTextChanged(object sender, TextChangedEventArgs e) =>
        PracticeHint.Visibility = PracticeBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void LearnClick(object sender, RoutedEventArgs e) =>
        controller.SetLearnFromCorrections(LearnBox.IsChecked == true);

    private void TeachClick(object sender, RoutedEventArgs e) => Teach();

    private void TeachKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Teach();
        }
    }

    /// <summary>Add, or Enter in either field. The section empties the row
    /// once the word is saved, and only if the row still holds what was
    /// added: the write runs off this thread, and the user may type meanwhile.</summary>
    private void Teach() => vocabulary.Teach(this);

    string IVocabularyTeachRow.Written => TeachWord.Text;

    string IVocabularyTeachRow.SoundsLike => TeachSoundsLike.Text;

    void IVocabularyTeachRow.Clear()
    {
        // Back to the word field only from where the Add came from: the
        // keyboard may have moved on while the word was being saved.
        var fromRow = TeachButton.IsKeyboardFocused
            || TeachWord.IsKeyboardFocusWithin || TeachSoundsLike.IsKeyboardFocusWithin;
        TeachWord.Clear();
        TeachSoundsLike.Clear();
        if (fromRow)
        {
            TeachWord.Focus();
        }
    }

    /// <summary>
    /// Save again, beside the unsaved warning and shown exactly while it is:
    /// "Saving…" and disabled while the write runs.
    /// </summary>
    /// <remarks>
    /// A button disabled under the keyboard focus loses it, and one collapsed
    /// under it leaves the keyboard on nothing. When Save again had the focus,
    /// the render that ends the save puts it back on the button, or on the
    /// word field when the save worked and the button has gone — but only
    /// while the keyboard is still where losing it left it
    /// (<see cref="KeyboardStillWithSaveAgain"/>), never off a control the
    /// user moved to during the save.
    /// </remarks>
    private void RenderSaveAgain(VocabularyViewState state)
    {
        var shown = state.NotSavedText is not null;
        SaveAgainButton.Content = state.SavingAgain ? DictationCopy.SavingAgain : WithAccessKey(DictationCopy.SaveAgain, "v");
        SaveAgainButton.IsEnabled = !state.SavingAgain;
        SaveAgainButton.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!state.SavingAgain && saveAgainHadKeyboard)
        {
            saveAgainHadKeyboard = false;
            if (KeyboardStillWithSaveAgain())
            {
                (shown ? (UIElement)SaveAgainButton : TeachWord).Focus();
            }
        }
    }

    /// <summary>The keyboard is on nothing, on Save again itself, or on one
    /// of its containers (the window, the scroll viewer): where WPF leaves it
    /// when the focused button is disabled. Anywhere else, the user put it
    /// there. Never seen on Windows.</summary>
    private bool KeyboardStillWithSaveAgain() =>
        Keyboard.FocusedElement is not DependencyObject focused
        || ReferenceEquals(focused, SaveAgainButton)
        || (focused is System.Windows.Media.Visual visual && visual.IsAncestorOf(SaveAgainButton));

    private void SaveAgainClick(object sender, RoutedEventArgs e)
    {
        saveAgainHadKeyboard = SaveAgainButton.IsKeyboardFocused;
        vocabulary.SaveAgain();
    }

    /// <summary>The placeholders show only over an empty field.</summary>
    private void TeachTextChanged(object sender, TextChangedEventArgs e)
    {
        TeachWordHint.Visibility = TeachWord.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TeachSoundsLikeHint.Visibility =
            TeachSoundsLike.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ForgetAllClick(object sender, RoutedEventArgs e)
    {
        vocabulary.AskForgetAll();
        CancelForgetAllButton.Focus();
    }

    private void ConfirmForgetAllClick(object sender, RoutedEventArgs e) => vocabulary.ConfirmForgetAll();

    private void CancelForgetAllClick(object sender, RoutedEventArgs e) => vocabulary.CancelForgetAll();

    private void MicrophoneSettingsClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(DictationCopy.MicrophoneSettingsUri) { UseShellExecute = true });

    private void MicrophoneRecheckClick(object sender, RoutedEventArgs e)
    {
        var before = Status.Text;
        controller.RecheckMicrophone(checkAgain: true);
        if (!LiveRegionAnnouncement.IsRepeat(before, Status.Text))
        {
            // A new sentence: SetLiveText has already put it up and raised it.
            return;
        }

        // Asked again and answered with the sentence already showing: nothing
        // would move and nothing would be said, and the button would seem not
        // to work. The line goes for a moment and comes back, which is seen,
        // and is announced again when it does. Opacity rather than an empty
        // string, so the line keeps its height and the buttons under it stay
        // where the pointer left them.
        recheckRepeated = Status.Text;
        Status.Opacity = 0;
        recheckPause.Stop();
        recheckPause.Start();
    }

    /// <summary>
    /// The end of that moment: the status line comes back and, while the
    /// user is still in this window and it is still the same sentence, is
    /// announced again. A sentence that replaced it in the meantime was
    /// announced when it arrived.
    /// </summary>
    private void EndRecheckPause()
    {
        recheckPause.Stop();
        Status.Opacity = 1;
        if (IsActive && Status.Text == recheckRepeated)
        {
            LiveRegion.Raise(Status);
        }

        recheckRepeated = null;
    }
}
