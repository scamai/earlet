namespace Dictation.Core;

/// <summary>
/// The environment overrides the Windows app reads — all of them. A
/// <c>DICTATION_*</c> name that is not listed here is not an override of this
/// product.
/// </summary>
/// <remarks>
/// Developer overrides, unset on an installed copy, where the bundled
/// <c>resources\</c> directory is the only right answer.
/// </remarks>
public static class DictationEnvironment
{
    /// <summary>The Python interpreter that runs the speech worker.</summary>
    public const string Python = "DICTATION_PYTHON";

    /// <summary>The directory that contains <c>transcription_worker/</c>.</summary>
    public const string Runtime = "DICTATION_RUNTIME";

    /// <summary>The CTranslate2 Whisper model directory.</summary>
    public const string ModelDirectory = "DICTATION_MODEL_DIR";

    /// <summary>
    /// The hold key, as a virtual-key code (decimal or <c>0x</c> hex) from the
    /// picker's list. Wins over the key chosen in the window, which then shows
    /// the override and cannot be changed.
    /// </summary>
    public const string HoldKey = Dictation.Services.DictationHotkeys.HoldKeyEnvVar;

    /// <summary>
    /// The live preview of the words on the indicator while the key is held:
    /// <c>1</c> turns it on; any other value, or none, leaves it off. Off by
    /// default on Windows, where the Mac has it on unless <c>0</c>: the
    /// preview's caption decodes run on the same CPU recogniser as the final
    /// pass, and what that costs depends on a model and hardware nobody has
    /// measured it on yet.
    /// </summary>
    public const string Preview = "DICTATION_PREVIEW";

    /// <summary>
    /// A directory: draw every indicator state to PNGs there, at 100, 150 and
    /// 200% in light, dark and high contrast, check them, and exit (non-zero
    /// when a shadow is cut or two busy pills differ in size), as the Mac's
    /// variable of the same name. Not the app: no log, no settings, no
    /// single-instance claim. Honoured by every build.
    /// </summary>
    public const string HudRenderDirectory = "DICTATION_HUD_RENDER_DIR";

    /// <summary>
    /// A directory: draw the window in each scenario of
    /// <see cref="DictationWindowRenderScenarios"/> to PNGs there, at three
    /// widths, three scales and three appearances, over inert parts, and
    /// exit (non-zero when a scenario or an image failed), as the Mac's
    /// variable of the same name. Honoured by every build.
    /// </summary>
    public const string WindowRenderDirectory = "DICTATION_WINDOW_RENDER_DIR";

    /// <summary>
    /// The in-hold watchdog that ends a take whose microphone stopped
    /// delivering (<see cref="DictationCaptureWatchdog"/>): <c>0</c> turns it
    /// off; any other value, or none, leaves it on, as the Mac's
    /// <c>dictation.capture.watchdog</c> flag. A field switch for a device
    /// whose buffering the 0.3 s gap misreads. Read once a run.
    /// </summary>
    public const string CaptureWatchdog = "DICTATION_CAPTURE_WATCHDOG";

    /// <summary>The whole set, for documentation and tests.</summary>
    public static IReadOnlyList<string> Overrides { get; } =
        [Python, Runtime, ModelDirectory, HoldKey, Preview, HudRenderDirectory, WindowRenderDirectory, CaptureWatchdog];

    /// <summary>Whether <paramref name="value"/>, the value of
    /// <see cref="Preview"/>, turns the live preview on: exactly <c>1</c>,
    /// around any spaces.</summary>
    public static bool PreviewIsOn(string? value) => value?.Trim() == "1";

    /// <summary>Whether <paramref name="value"/>, the value of
    /// <see cref="CaptureWatchdog"/>, leaves the watchdog on: anything but
    /// exactly <c>0</c>, around any spaces.</summary>
    public static bool CaptureWatchdogIsOn(string? value) => value?.Trim() != "0";

    /// <summary>The process environment, as the app reads it.</summary>
    public static string? Process(string name) => Environment.GetEnvironmentVariable(name);
}
