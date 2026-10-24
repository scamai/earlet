using Dictation.Core;
using Dictation.Core.Learning;
using Microsoft.Extensions.Logging;

namespace Dictation.App;

/// <summary>
/// What <see cref="MainWindow"/> builds its controller and Vocabulary section
/// over: the user's own (<see cref="ForUser"/>, the app), or the window render
/// mode's inert ones (<see cref="ForRender"/>, <see cref="DictationRenderMode"/>).
/// The window's composition is otherwise the same in both, so a render draws
/// the window the app shows.
/// </summary>
/// <param name="Platform">The Win32 edges.</param>
/// <param name="Settings">The settings file.</param>
/// <param name="Learning">The vocabulary, shared by the controller's
/// correction watch and the Vocabulary section.</param>
/// <param name="Environment">The <c>DICTATION_*</c> overrides.</param>
/// <param name="AppDirectory">Where <c>resources\</c> is looked for.</param>
/// <param name="Options">The controller's knobs; null is the product's.</param>
/// <param name="Corrections">What the Vocabulary section listens to, given
/// the controller's correction watch: the watch itself in the app.</param>
/// <param name="Indicator">Whether the floating indicator is made.</param>
/// <param name="TrayIcon">Whether the notification-area icon is shown.</param>
public sealed record DictationWindowParts(
    IDictationPlatform Platform,
    DictationSettingsStore Settings,
    DictationLearning Learning,
    Func<string, string?> Environment,
    string AppDirectory,
    DictationControllerOptions? Options,
    Func<CorrectionWatch?, ICorrectionWatchEvents?> Corrections,
    bool Indicator,
    bool TrayIcon)
{
    /// <summary>The app's: this user's settings and vocabulary, Windows, the
    /// installed <c>resources\</c>, the indicator and the icon.</summary>
    internal static DictationWindowParts ForUser(ILoggerFactory logs)
    {
        var paths = DictationPaths.ForCurrentUser();
        return new DictationWindowParts(
            new WindowsDictationPlatform(logs),
            new DictationSettingsStore(paths.SettingsFile, logs.CreateLogger<DictationSettingsStore>()),
            DictationLearning.ForUser(paths, logs),
            DictationEnvironment.Process,
            AppContext.BaseDirectory,
            Options: null,
            Corrections: watch => watch,
            Indicator: true,
            TrayIcon: true);
    }

    /// <summary>The window render mode's: <paramref name="parts"/>' scratch
    /// files and inert platform, the correction events a scenario can raise,
    /// no indicator and no icon.</summary>
    internal static DictationWindowParts ForRender(DictationRenderComposition parts) =>
        new(
            parts.Platform,
            parts.Settings,
            parts.Learning,
            parts.Environment,
            parts.AppDirectory,
            parts.Options,
            parts.Relay,
            Indicator: false,
            TrayIcon: false);
}
