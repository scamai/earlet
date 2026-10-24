using System.Diagnostics;
using System.IO;
using Dictation.Core;
using Dictation.Infrastructure;
using Dictation.Platform.Audio;
using Dictation.Platform.Insert;
using Dictation.Platform.Transcription;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.App;

/// <summary>
/// The Win32 half of the composition: the keyboard hook, WASAPI, the Python
/// worker process and the clipboard/paste/UI Automation edges. Everything that
/// decides anything is in <see cref="DictationController"/>.
/// </summary>
internal sealed class WindowsDictationPlatform(ILoggerFactory logs) : IDictationPlatform
{
    public IMicrophoneAccessProbe MicrophoneAccess { get; } =
        new WindowsMicrophoneAccessProbe(logs.CreateLogger<WindowsMicrophoneAccessProbe>());

    public ILaunchAtLogin LaunchAtLogin { get; } = new WindowsLaunchAtLogin(
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Dictation.exe"),
        logs.CreateLogger<WindowsLaunchAtLogin>());

    /// <summary>Copy last text's writer: the one the insertion and the
    /// refused-paste fallback write through, which marks its text to stay out
    /// of clipboard history and cloud sync. One attempt, no retry.</summary>
    public IClipboard Clipboard { get; } = new Win32Clipboard(logs.CreateLogger<Win32Clipboard>());

    /// <summary>Whether the window in front runs as administrator, asked from
    /// the poll and cached for the process in front (<see cref="AdminWindowWatch"/>).</summary>
    public IForegroundElevation ForegroundElevation { get; } =
        new Elevation(new ForegroundElevation(logs.CreateLogger<ForegroundElevation>()));

    /// <summary>The countdown's sound: Windows' Asterisk sound, as the user's
    /// sound scheme sets it (silent under "No sounds"). Played on the
    /// system's own thread; built, never heard on Windows yet.</summary>
    public void PlayAlertSound() => System.Media.SystemSounds.Asterisk.Play();

    public bool OpenFolder(string path)
    {
        try
        {
            // Created first: Explorer cannot open a folder the log never made.
            Directory.CreateDirectory(path);
            using var explorer = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception error)
        {
            logs.CreateLogger<WindowsDictationPlatform>().LogWarning(error, "Explorer did not open the log folder");
            return false;
        }
    }

    public bool ShowInFolder(string file)
    {
        try
        {
            // Moved or renamed since it started: Explorer would open a folder
            // of its own choosing and say nothing.
            if (!File.Exists(file))
            {
                logs.CreateLogger<WindowsDictationPlatform>().LogWarning(
                    "This copy is no longer at the path Windows reported; File Explorer was not opened");
                return false;
            }

            // By its full path: a bare explorer.exe is looked for in this
            // app's folder and the current one before Windows'.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            // Explorer's own switch: open the folder with the file selected.
            // Started is all this learns: Explorer reports nothing about
            // whether it could select the file.
            using var process = Process.Start(
                new ProcessStartInfo(explorer, DictationInstallation.SelectInExplorerArguments(file))
                {
                    UseShellExecute = false,
                });
            return true;
        }
        catch (Exception error)
        {
            // The type only: the message can hold the path, and the path the user's name.
            logs.CreateLogger<WindowsDictationPlatform>().LogWarning(
                "Explorer did not show this copy ({Error})", error.GetType().Name);
            return false;
        }
    }

    public IDictationTrigger CreateTrigger(HotkeyBinding binding, bool cancelOnChord, bool handsFree) =>
        new Trigger(new LowLevelHoldKeyRegistration(
            binding,
            logs.CreateLogger<LowLevelHoldKeyRegistration>(),
            triggerMode: handsFree ? HoldKeyTriggerMode.HoldOrDoubleTapHandsFree : HoldKeyTriggerMode.HoldOnly,
            cancelOnChord: cancelOnChord));

    public IAudioCaptureDevice CreateMicrophone() =>
        new WasapiMicrophoneCaptureDevice(logs.CreateLogger<WasapiMicrophoneCaptureDevice>());

    public DictationWorker CreateWorker(DictationWorkerPlan plan)
    {
        var process = new PythonAsrWorkerProcess(
            new AsrWorkerOptions(plan.PythonExecutable, plan.RuntimeDirectory,
                WorkerEnvironment: plan.WorkerEnvironment),
            logs.CreateLogger<PythonAsrWorkerProcess>())
        {
            StderrLogLevel = LogLevel.Information,
        };
        return new DictationWorker(process, () => process.StderrLogLevel = LogLevel.Debug);
    }

    public ISpanObserverTarget CreateSpanObserverTarget(SpanObserverKeyboardSignal keyboard, SpanObserverOptions options) =>
        UiaSpanObserverTarget.Resolve(logs.CreateLogger<UiaSpanObserverTarget>(), keyboard, options);

    public DictationInsertionEdges CreateInsertionEdges()
    {
        var target = new Win32FocusedApplicationResolver(logs.CreateLogger<Win32FocusedApplicationResolver>());
        var clipboard = new Win32ReadableClipboard(
            new Win32Clipboard(logs.CreateLogger<Win32Clipboard>()),
            logs.CreateLogger<Win32ReadableClipboard>());
        return new DictationInsertionEdges(
            target,
            clipboard,
            new Win32PasteKeystroke(logs.CreateLogger<Win32PasteKeystroke>()),
            new OwnWindowSecureFieldProbe(
                new UiaSecureFieldProbe(logs.CreateLogger<UiaSecureFieldProbe>()),
                ForegroundWindow.IsThisProcess));
    }

    /// <summary>The hook, with the members the controller polls.</summary>
    private sealed class Trigger(LowLevelHoldKeyRegistration inner) : IDictationTrigger
    {
        public HotkeyBinding Binding => inner.Binding;

        public bool DictationInFlight
        {
            get => inner.DictationInFlight;
            set => inner.DictationInFlight = value;
        }

        public bool EscapeAsksFirst
        {
            get => inner.EscapeAsksFirst;
            set => inner.EscapeAsksFirst = value;
        }

        public Action<long, int>? KeyboardInputObserved
        {
            get => inner.KeyboardInputObserved;
            set => inner.KeyboardInputObserved = value;
        }

        public bool Register() => inner.Register();

        public void Unregister() => inner.Unregister();

        public bool IsKeyDown() => inner.IsKeyDown();

        public bool IsLatched => inner.IsLatched;

        public void EndLatch() => inner.EndLatch();

        public LatchEnd ConsumeLatchEnd() => inner.ConsumeLatchEnd();

        public TriggerCancel ConsumeCancelRequest() =>
            !inner.ConsumeCancelRequest(out var byChord, out var asked) ? TriggerCancel.None
            : asked ? TriggerCancel.EscapeAsked
            : byChord ? TriggerCancel.Chord
            : TriggerCancel.Escape;

        // The hook watch (DictationHookWatch): called from the poll, on the
        // thread that installed the hook, never from its callback.
        public HookReading ReadHook() =>
            new(inner.IsKeyDownNow(), inner.HookSawKeyDown, inner.ObservedKeyEvents, inner.TriggerKeyEvents);

        public bool ProbeHook() => inner.ProbeHook();

        public void Resync(bool keyDown) => inner.Resync(keyDown, Environment.TickCount64);

        public bool Reinstall() => inner.Reinstall();

        public void Dispose() => inner.Dispose();
    }

    /// <summary>The Win32 elevation probe, as the controller asks it.</summary>
    private sealed class Elevation(ForegroundElevation inner) : IForegroundElevation
    {
        public bool ThisProcessElevated => inner.ThisProcessElevated;

        public int ForegroundProcessId() => inner.ForegroundProcessId();

        public bool? IsElevated(int processId) => inner.IsElevated(processId);
    }
}
