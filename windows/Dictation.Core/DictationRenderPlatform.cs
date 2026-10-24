using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;

namespace Dictation.Core;

/// <summary>
/// The Win32 edges as the window render mode (<c>DICTATION_WINDOW_RENDER_DIR</c>)
/// needs them: nothing here opens a microphone, a process, a keyboard hook,
/// the clipboard, Explorer or the registry. The Mac's inert seams in
/// <c>DictationWindowRender.swift</c> (<c>RenderCapture</c>,
/// <c>RenderWorker</c>, <c>RenderTrigger</c>…), for the Windows controller.
/// </summary>
/// <remarks>
/// <para>
/// The render builds the real window, controller and vocabulary over this and
/// drives them into each scenario through their public members
/// (<see cref="DictationWindowRenderScript"/>). A scenario sets the switches
/// below: a blocked microphone, a hook that will not register, or one Windows
/// removed, a window running as administrator in front, a clipboard that
/// refuses the words, a field that becomes a password field during the
/// decode, a sign-in entry Windows refuses or Settings switched off, a
/// microphone unplugged mid-hold, and a worker that holds its answer, so a
/// picture can be taken of what shows while it waits.
/// </para>
/// <para>
/// The speech worker is the real framing (a 4-byte big-endian length, then
/// JSON) over two in-process pipes, answered on a pool thread: the worker
/// host and the slot are the app's own, and only the Python process is
/// missing. Every transcription answers <see cref="DefaultTranscript"/>.
/// </para>
/// </remarks>
public sealed class DictationRenderPlatform : IDictationPlatform, IDisposable
{
    /// <summary>The Mac render's sentence, English then Chinese, long enough
    /// to wrap in the Recent result section.</summary>
    public const string DefaultTranscript =
        "Let's review the first draft tomorrow. 明天下午一起看看第一版，确认文字和操作都能自然衔接。";

    private readonly List<RenderWorkerProcess> _workers = [];
    private readonly RenderField _field = new();
    private readonly RenderClipboard _clipboard;
    private readonly RenderPaste _paste;
    private readonly RenderForeground _foreground;

    /// <summary>Completed by <see cref="ReleaseHolds"/>: what a held worker
    /// answer waits for.</summary>
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The field the words go to is a password field now.</summary>
    private volatile bool _fieldIsSecure;

    public DictationRenderPlatform()
    {
        _clipboard = new RenderClipboard(this);
        _paste = new RenderPaste(this);
        _foreground = new RenderForeground(this);
    }

    /// <summary>What Windows answers about the microphone.</summary>
    public MicrophoneAccess Access { get; set; } = Dictation.Services.MicrophoneAccess.Allowed;

    /// <summary>False: the hold key's hook will not register.</summary>
    public bool HookRegisters { get; set; } = true;

    /// <summary>False: the clipboard will not take the words, so the paste,
    /// which goes through it, never happens, and the fallback cannot leave
    /// them there either: Dictation keeps them for Copy last text.</summary>
    public bool ClipboardAccepts { get; set; } = true;

    /// <summary>True: a window running as administrator is in front, where
    /// the hook hears nothing (<see cref="IForegroundElevation"/>).</summary>
    public bool AdministratorWindowInFront { get; set; }

    /// <summary>True: the field the words go to becomes a password field
    /// while they are transcribed, so the check at the paste refuses them.</summary>
    public bool PasswordFieldDuringTranscription { get; set; }

    /// <summary>True: the worker holds its answer to the speech model's load
    /// until <see cref="ReleaseHolds"/>, so an Enable stays "Starting".</summary>
    public bool HoldStart { get; set; }

    /// <summary>True: the worker refuses every transcription, so a take is
    /// kept for Recover last dictation (the Saved recording section).</summary>
    public bool TranscriptionRefused { get; set; }

    /// <summary>True: the worker holds its transcript until
    /// <see cref="ReleaseHolds"/>, so a released hold stays "Transcribing".</summary>
    public bool HoldTranscription { get; set; }

    /// <summary>The sign-in entry, whose switches a scenario sets.</summary>
    public RenderLaunchAtLogin SignIn { get; } = new();

    /// <summary>The hold key's trigger the controller armed last, or null.</summary>
    public RenderTrigger? Trigger { get; private set; }

    /// <summary>The microphone the controller opened last, or null.</summary>
    public RenderMicrophone? Microphone { get; private set; }

    /// <summary>How many speech workers the controller started.</summary>
    public int WorkersStarted
    {
        get
        {
            lock (_workers)
            {
                return _workers.Count;
            }
        }
    }

    public IMicrophoneAccessProbe MicrophoneAccess => new RenderAccess(this);

    public ILaunchAtLogin LaunchAtLogin => SignIn;

    public IClipboard Clipboard => _clipboard;

    public IForegroundElevation ForegroundElevation => _foreground;

    /// <summary>The render draws; it plays nothing.</summary>
    public void PlayAlertSound()
    {
    }

    public IDictationTrigger CreateTrigger(HotkeyBinding binding, bool cancelOnChord, bool handsFree) =>
        Trigger = new RenderTrigger(binding, HookRegisters);

    public IAudioCaptureDevice CreateMicrophone() => Microphone = new RenderMicrophone();

    public DictationWorker CreateWorker(DictationWorkerPlan plan)
    {
        var worker = new RenderWorkerProcess(this);
        lock (_workers)
        {
            _workers.Add(worker);
        }

        return new DictationWorker(worker, () => { });
    }

    public DictationInsertionEdges CreateInsertionEdges() =>
        new(new RenderTarget(), _clipboard, _paste, new RenderSecureField(this));

    /// <summary>Never opens Explorer; says it did.</summary>
    public bool OpenFolder(string path) => true;

    /// <summary>Never opens Explorer; says it did.</summary>
    public bool ShowInFolder(string file) => true;

    /// <summary>Let every held worker answer go: the load and the
    /// transcript a scenario held for its picture.</summary>
    public void ReleaseHolds() => _released.TrySetResult();

    /// <summary>The field the words were pasted into, which the user never
    /// changes: a correction watch over it keeps checking until the render
    /// lets go of the controller.</summary>
    public ISpanObserverTarget CreateSpanObserverTarget(SpanObserverKeyboardSignal keyboard, SpanObserverOptions options)
    {
        _field.Keyboard = keyboard;
        return _field;
    }

    public void Dispose()
    {
        ReleaseHolds();
        RenderWorkerProcess[] workers;
        lock (_workers)
        {
            workers = [.. _workers];
        }

        foreach (var worker in workers)
        {
            worker.Dispose();
        }
    }

    /// <summary>The process every insertion and the field name: a stand-in
    /// for Notepad, with no window title anyone could read.</summary>
    private const string TargetProcess = "notepad";

    private const int TargetProcessId = 4242;

    /// <summary>The window running as administrator, when one is in front.</summary>
    private const int AdministratorProcessId = 4343;

    /// <summary>What a held worker answer waits for: <see cref="ReleaseHolds"/>,
    /// or the worker stopping.</summary>
    private Task Held(bool hold, CancellationToken ct) => hold ? _released.Task.WaitAsync(ct) : Task.CompletedTask;

    // ─── The hold key and the microphone ────────────────────────────────

    /// <summary>
    /// The hold key: down while a scenario says it is. No latch, no cancel,
    /// no keyboard feed of its own. Windows' view of the key and the hook's
    /// are kept apart, as the hook watch reads them
    /// (<see cref="IHookWatchTarget"/>): a scenario removes the hook
    /// (<see cref="HookAlive"/>), and the user's key then reaches Windows but
    /// not the hook, whose view goes stale.
    /// </summary>
    public sealed class RenderTrigger(HotkeyBinding binding, bool registers) : IDictationTrigger
    {
        /// <summary>A press the dead hook never saw, adopted as down by the
        /// hook watch and holding nothing.</summary>
        private bool _adopted;

        public HotkeyBinding Binding { get; } = binding;

        /// <summary>The key is held, as the hook saw it.</summary>
        public bool Down { get; set; }

        /// <summary>Whether the hook still receives keys; false: Windows removed it.</summary>
        public bool HookAlive { get; set; } = true;

        /// <summary>Whether Windows lets the hook watch put a removed hook back.</summary>
        public bool ReinstallSucceeds { get; set; } = true;

        /// <summary>Windows' own view of the key; null: the hook's.</summary>
        public bool? WindowsKeyDown { get; set; }

        /// <summary>Keyboard events the hook has received.</summary>
        public long HookEvents { get; private set; }

        /// <summary>Of those, the hold key's own.</summary>
        public long TriggerEvents { get; private set; }

        public bool DictationInFlight { get; set; }

        /// <summary>No scenario presses Escape.</summary>
        public bool EscapeAsksFirst { get; set; }

        public Action<long, int>? KeyboardInputObserved { get; set; }

        public bool IsLatched => false;

        public bool Register() => registers;

        public void Unregister()
        {
        }

        public bool IsKeyDown() => Down;

        public TriggerCancel ConsumeCancelRequest() => TriggerCancel.None;

        public void EndLatch()
        {
        }

        public LatchEnd ConsumeLatchEnd() => LatchEnd.None;

        /// <summary>The user presses or lets go of the hold key: Windows sees
        /// it, and the hook too while it is alive.</summary>
        public void UserHoldKey(bool down)
        {
            WindowsKeyDown = down;
            if (!HookAlive)
            {
                return;
            }

            HookEvents++;
            TriggerEvents++;
            Down = down;
            _adopted &= down;
        }

        public HookReading ReadHook()
        {
            var hookView = Down || _adopted;
            return new HookReading(WindowsKeyDown ?? hookView, hookView, HookEvents, TriggerEvents);
        }

        public bool ProbeHook()
        {
            if (HookAlive)
            {
                // The injected down and up, both through the hook.
                HookEvents += 2;
            }

            return true;
        }

        public void Resync(bool keyDown)
        {
            if (keyDown)
            {
                _adopted = true;
            }
            else
            {
                Down = false;
                _adopted = false;
            }
        }

        public bool Reinstall() => HookAlive = ReinstallSucceeds;

        public void Dispose() => Down = false;
    }

    /// <summary>A microphone that delivers only what a scenario speaks, and
    /// says when a scenario unplugs it (<see cref="IAudioCaptureEndpointWatch"/>).</summary>
    public sealed class RenderMicrophone : IAudioCaptureDevice, IAudioCaptureEndpointWatch
    {
        private const int Rate = 16_000;

        public event EventHandler<AudioChunk>? BufferCaptured;

        public event EventHandler<AudioCaptureEndpointChange>? EndpointChanged;

        public bool Open { get; private set; }

        public int Capture { get; private set; }

        public bool Start()
        {
            Capture++;
            return Open = true;
        }

        public void Stop() => Open = false;

        public void Dispose() => Open = false;

        /// <summary>The microphone in use is unplugged, and Windows names
        /// <paramref name="next"/> as the one the next hold opens. Raised on
        /// the thread pool, as WASAPI's notification is.</summary>
        public void Unplug(string? next)
        {
            var change = new AudioCaptureEndpointChange(AudioCaptureEndpointChangeKind.Removed, next, Capture);
            _ = Task.Run(() => EndpointChanged?.Invoke(this, change));
        }

        /// <summary>Half a second of a 440 Hz tone, loud enough to pass the
        /// session's quiet-room floor, in one buffer.</summary>
        public void Speak()
        {
            var samples = new float[Rate / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / Rate);
            }

            BufferCaptured?.Invoke(this, new AudioChunk(samples, Rate, 1));
        }
    }

    private sealed class RenderAccess(DictationRenderPlatform owner) : IMicrophoneAccessProbe
    {
        public MicrophoneAccess Probe() => owner.Access;
    }

    /// <summary>The sign-in entry: written when asked, unless Windows
    /// refuses (<see cref="Refuses"/>), and off in Settings when a scenario
    /// says so.</summary>
    public sealed class RenderLaunchAtLogin : ILaunchAtLogin
    {
        public bool IsEnabled { get; private set; }

        /// <summary>True: the entry names this copy but Settings → Apps →
        /// Startup switched it off.</summary>
        public bool SwitchedOffInSettings { get; set; }

        /// <summary>True: Windows refuses every change.</summary>
        public bool Refuses { get; set; }

        public bool TrySet(bool enabled)
        {
            if (Refuses)
            {
                return false;
            }

            IsEnabled = enabled;
            return true;
        }
    }

    /// <summary>The window in front: the stand-in Notepad, or a window
    /// running as administrator while a scenario says one is.</summary>
    private sealed class RenderForeground(DictationRenderPlatform owner) : IForegroundElevation
    {
        public bool ThisProcessElevated => false;

        public int ForegroundProcessId() => owner.AdministratorWindowInFront ? AdministratorProcessId : TargetProcessId;

        public bool? IsElevated(int processId) => processId == AdministratorProcessId;
    }

    // ─── Insertion ──────────────────────────────────────────────────────

    private sealed class RenderTarget : IFocusedApplicationResolver
    {
        public FocusedApplicationResult Resolve() =>
            FocusedApplicationResult.Resolved(TargetProcess, TargetProcessId, string.Empty);
    }

    /// <summary>The field the words go to: an ordinary one, or a password
    /// field once the worker has had the words while a scenario says so.</summary>
    private sealed class RenderSecureField(DictationRenderPlatform owner) : ISecureFieldProbe
    {
        public SecureFieldState Probe() => owner._fieldIsSecure ? SecureFieldState.Secure : SecureFieldState.NotSecure;
    }

    /// <summary>The render's own clipboard: never Windows'.</summary>
    private sealed class RenderClipboard(DictationRenderPlatform owner) : IClipboard, IClipboardTextSnapshot
    {
        private readonly Lock _gate = new();
        private string? _text;

        public string? Text
        {
            get
            {
                lock (_gate)
                {
                    return _text;
                }
            }
        }

        public bool TrySet(string text)
        {
            if (!owner.ClipboardAccepts)
            {
                return false;
            }

            lock (_gate)
            {
                _text = text;
            }

            return true;
        }

        public bool TryRead(out string? text)
        {
            text = Text;
            return true;
        }
    }

    /// <summary>The paste keystroke: lands what the clipboard holds in the
    /// field.</summary>
    private sealed class RenderPaste(DictationRenderPlatform owner) : IPasteKeystroke
    {
        public bool TrySend()
        {
            owner._field.Paste(owner._clipboard.Text ?? string.Empty);
            return true;
        }
    }

    /// <summary>The field, as UI Automation would show it to the span
    /// observer: the pasted words with the caret after them, unchanged.</summary>
    private sealed class RenderField : ISpanObserverTarget
    {
        private readonly Lock _gate = new();
        private string _text = string.Empty;

        public SpanObserverKeyboardSignal? Keyboard { get; set; }

        public SpanObserverTargetIdentity Identity { get; } = new(TargetProcess, TargetProcessId, "Edit", 7, false);

        public SpanObserverKeyboardInput? LastKeyboardInput => Keyboard?.Last;

        public void Paste(string text)
        {
            lock (_gate)
            {
                _text = text;
            }
        }

        public SpanObserverResult<SecureFieldState> ProbeSecureField() =>
            SpanObserverResult<SecureFieldState>.Success(SecureFieldState.NotSecure);

        public SpanObserverResult<SpanObserverSelection> GetSelection()
        {
            lock (_gate)
            {
                return SpanObserverResult<SpanObserverSelection>.Success(new SpanObserverSelection(_text.Length, 0));
            }
        }

        public SpanObserverResult<string> ReadRange(int start, int length)
        {
            lock (_gate)
            {
                var from = Math.Clamp(start, 0, _text.Length);
                var to = Math.Clamp(start + length, from, _text.Length);
                return SpanObserverResult<string>.Success(_text[from..to]);
            }
        }

        public SpanObserverResult<string> ReadText(int maxChars)
        {
            lock (_gate)
            {
                return SpanObserverResult<string>.Success(_text.Length <= maxChars ? _text : _text[..maxChars]);
            }
        }

        public IDisposable? Subscribe(Action<SpanObserverSignal> handler) => null;
    }

    // ─── The speech worker ──────────────────────────────────────────────

    /// <summary>The worker process the host frames requests to, served on a
    /// pool thread over two in-process pipes.</summary>
    private sealed class RenderWorkerProcess(DictationRenderPlatform owner) : IAsrWorkerProcess
    {
        private AnonymousPipeServerStream? _hostToWorker;
        private AnonymousPipeClientStream? _workerIn;
        private AnonymousPipeServerStream? _workerToHost;
        private AnonymousPipeClientStream? _hostIn;
        private CancellationTokenSource? _stop;

        public bool IsAlive { get; private set; }

        public Stream StandardIn => _hostToWorker ?? throw new InvalidOperationException("not started");

        public Stream StandardOut => _hostIn ?? throw new InvalidOperationException("not started");

        public Task StartAsync(CancellationToken ct)
        {
            if (IsAlive)
            {
                return Task.CompletedTask;
            }

            _hostToWorker = new AnonymousPipeServerStream(PipeDirection.Out);
            _workerIn = new AnonymousPipeClientStream(PipeDirection.In, _hostToWorker.ClientSafePipeHandle);
            _workerToHost = new AnonymousPipeServerStream(PipeDirection.Out);
            _hostIn = new AnonymousPipeClientStream(PipeDirection.In, _workerToHost.ClientSafePipeHandle);
            _stop = new CancellationTokenSource();
            IsAlive = true;
            var input = _workerIn;
            var output = _workerToHost;
            var token = _stop.Token;
            _ = Task.Run(() => ServeAsync(input, output, token), CancellationToken.None);
            return Task.CompletedTask;
        }

        private async Task ServeAsync(Stream input, Stream output, CancellationToken ct)
        {
            try
            {
                var header = new byte[4];
                while (!ct.IsCancellationRequested)
                {
                    if (await AsrFraming.ReadExactAsync(input, header, ct) < 4)
                    {
                        return;
                    }

                    var payload = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
                    await AsrFraming.ReadExactAsync(input, payload, ct);
                    using var request = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
                    var op = request.RootElement.GetProperty("op").GetString();
                    switch (op)
                    {
                        case "init":
                            await owner.Held(owner.HoldStart, ct);
                            break;
                        case "transcribe":
                            // The user clicks into a password field while the words decode.
                            owner._fieldIsSecure |= owner.PasswordFieldDuringTranscription;
                            await owner.Held(owner.HoldTranscription, ct);
                            break;
                    }

                    object reply = op switch
                    {
                        "init" => new { ok = true, on_npu = false },
                        "transcribe" when owner.TranscriptionRefused => new { ok = false, error = "refused in a render" },
                        "transcribe" => new { ok = true, text = DefaultTranscript },
                        "stream_start" => new { ok = false, error = "no live preview in a render" },
                        _ => new { ok = true },
                    };
                    await AsrFraming.WriteFrameAsync(output, reply, ct);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested || !IsAlive)
            {
                // Stopped under the loop.
            }
            catch (IOException)
            {
                // The host closed its end.
            }
        }

        public Task StopAsync(TimeSpan grace, CancellationToken ct)
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            IsAlive = false;
            try
            {
                _stop?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _hostToWorker?.Dispose();
            _workerToHost?.Dispose();
            _workerIn?.Dispose();
            _hostIn?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
