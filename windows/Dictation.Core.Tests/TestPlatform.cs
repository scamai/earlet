using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Dictation.Core.Learning;
using Dictation.Core.Tests.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

// ─── The fakes the controller tests compose against ─────────────────────────
//
// Only the Win32 edges are fake: the hook's key state, the WASAPI device, the
// Python process, the clipboard, the paste keystroke, the focus/password
// probes and the UI Automation view of the field a paste lands in. The
// session, push-to-talk controller, microphone tap, worker host, framing,
// focused-text insertion, span observer and learning policy are the real
// classes the app runs.

internal sealed partial class FakeTrigger(HotkeyBinding binding, bool cancelOnChord, bool handsFree) : IDictationTrigger
{
    public HotkeyBinding Binding { get; } = binding;
    public bool CancelOnChord { get; } = cancelOnChord;
    public bool HandsFree { get; } = handsFree;
    public bool Down { get; set; }
    public TriggerCancel PendingCancel { get; set; }
    public bool RegisterSucceeds { get; set; } = true;
    public bool Registered { get; private set; }
    public bool Disposed { get; private set; }

    private bool _dictationInFlight;

    public bool DictationInFlight
    {
        get => _dictationInFlight;
        set
        {
            _dictationInFlight = value;
            if (Gesture is not null)
            {
                Gesture.DictationInFlight = value;
            }
        }
    }

    // ─── The real gesture, optionally ───────────────────────────────────
    //
    // With UseGesture the fake runs the hook's own state machine: tests feed
    // key events at GestureTime and the key, latch and swallow decisions are
    // HoldKeyState's, in the mode the controller asked for. Without it, Down
    // and Latched are set by hand.

    /// <summary>The hook's state machine, or null when the test sets
    /// <see cref="Down"/> and <see cref="Latched"/> itself.</summary>
    public HoldKeyState? Gesture { get; private set; }

    /// <summary>The hook's clock, in milliseconds; tests move it with the
    /// controller's.</summary>
    public long GestureTime { get; set; } = 1_000;

    public void UseGesture() => Gesture = new HoldKeyState(
        (int)Binding.VirtualKey,
        HandsFree ? HoldKeyTriggerMode.HoldOrDoubleTapHandsFree : HoldKeyTriggerMode.HoldOnly,
        cancelOnChord: CancelOnChord)
    {
        DictationInFlight = DictationInFlight,
    };

    /// <summary>One key event through the hook's path: the state machine,
    /// then the cancel latch, as <c>LowLevelHoldKeyRegistration</c> does.
    /// Returns the decision, so a test can see what was swallowed.</summary>
    public HoldKeyTransition KeyEvent(int virtualKey, int message)
    {
        HookEvents++;
        if (virtualKey == (int)Binding.VirtualKey)
        {
            TriggerEvents++;
        }

        var transition = Gesture!.Observe(virtualKey, message, GestureTime);
        if ((transition & HoldKeyTransition.CancelRequested) != 0)
        {
            PendingCancel = virtualKey == HoldKeyState.VkEscape ? TriggerCancel.Escape : TriggerCancel.Chord;
        }
        else if ((transition & HoldKeyTransition.EscapeAsked) != 0 && PendingCancel == TriggerCancel.None)
        {
            PendingCancel = TriggerCancel.EscapeAsked;
        }

        return transition;
    }

    /// <summary>The latch, as a test sets it without the gesture.</summary>
    public bool Latched { get; set; }

    public bool IsLatched => Gesture?.IsLatched ?? Latched;

    /// <summary>How many times the controller ended a latch from outside the
    /// keyboard stream.</summary>
    public int EndLatchCalls { get; private set; }

    public void EndLatch()
    {
        EndLatchCalls++;
        Latched = false;
        Gesture?.EndLatch();
    }

    public LatchEnd ConsumeLatchEnd() => Gesture?.ConsumeLatchEnd() ?? LatchEnd.None;

    private Action<long, int>? _keyboardInputObserved;

    /// <summary>The hook's key-down feed, as the controller left it. Read by
    /// the fake field when the user types, on the observer's thread.</summary>
    public Action<long, int>? KeyboardInputObserved
    {
        get => Volatile.Read(ref _keyboardInputObserved);
        set => Volatile.Write(ref _keyboardInputObserved, value);
    }

    public bool Register() => Registered = RegisterSucceeds;

    public void Unregister() => Registered = false;

    public bool IsKeyDown() => Gesture?.IsHeldAt(GestureTime) ?? (Down || Latched);

    public TriggerCancel ConsumeCancelRequest()
    {
        var pending = PendingCancel;
        PendingCancel = TriggerCancel.None;
        // What the hook does just after the poll read it, once (TakeFakes.cs).
        if (Interlocked.Exchange(ref _afterConsume, null) is { } after)
        {
            after();
        }

        return pending;
    }

    public void Dispose()
    {
        Disposed = true;
        Registered = false;
        // As the registration's teardown does: nothing stays held or latched.
        Gesture?.Release();
    }
}

// Synthetic speech, a room and clicks for the silence tracker: SyntheticSpeech.cs.
internal sealed partial class FakeMicrophone : IAudioCaptureDevice, IAudioCaptureEndpointWatch
{
    public event EventHandler<AudioChunk>? BufferCaptured;

    public event EventHandler<AudioCaptureEndpointChange>? EndpointChanged;

    public bool Open { get; private set; }

    public bool StartSucceeds { get; set; } = true;

    /// <summary>How many times the endpoint was asked to open.</summary>
    public int Starts { get; private set; }

    /// <summary>How many captures opened, as the WASAPI device counts them.</summary>
    public int Capture { get; private set; }

    public bool Start()
    {
        Starts++;
        // A device that takes a while to open (TakeFakes.cs).
        Opening?.Invoke();
        if (StartSucceeds)
        {
            Capture++;
        }

        return Open = StartSucceeds;
    }

    /// <summary>Windows says the microphone changed under the capture,
    /// numbered <paramref name="capture"/> (the open one by default), as the
    /// WASAPI device reports it. On the test's thread; the controller posts
    /// it to its own either way.</summary>
    public void Change(AudioCaptureEndpointChangeKind kind, string? next, int? capture = null) =>
        EndpointChanged?.Invoke(this, new AudioCaptureEndpointChange(kind, next, capture ?? Capture));

    /// <summary>How many handlers hear about a change now.</summary>
    public int ChangeListeners => EndpointChanged?.GetInvocationList().Length ?? 0;

    public void Stop() => Open = false;

    public void Dispose() => Open = false;

    /// <summary>Half a second of a 440 Hz tone, loud enough to pass the
    /// session's quiet-room floor.</summary>
    public void Speak() => Speak(TimeSpan.FromSeconds(0.5));

    /// <summary><paramref name="length"/> of the same tone.</summary>
    public void Speak(TimeSpan length)
    {
        const int rate = 16_000;
        var samples = new float[(int)(rate * length.TotalSeconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        }

        BufferCaptured?.Invoke(this, new AudioChunk(samples, rate, 1));
    }

    /// <summary>Half a second of a quiet room: samples, but nothing above
    /// the session's audible floor, so nothing is sent to the recogniser.</summary>
    public void Hush() => BufferCaptured?.Invoke(this, new AudioChunk(new float[16_000 / 2], 16_000, 1));

    /// <summary>One 10 ms buffer, the size WASAPI delivers, of a tone whose
    /// loudest sample is exactly <paramref name="peak"/>.</summary>
    public void Buffer(float peak)
    {
        const int rate = 16_000;
        var samples = new float[rate / 100];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = peak * 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        }

        samples[1] = -peak;
        BufferCaptured?.Invoke(this, new AudioChunk(samples, rate, 1));
    }

    /// <summary>How many handlers the endpoint's buffers go to now.</summary>
    public int Listeners => BufferCaptured?.GetInvocationList().Length ?? 0;
}

/// <summary>
/// A speech worker over two in-process pipes that speaks the real framing:
/// 4-byte big-endian length, then JSON. It records every request frame
/// byte for byte.
/// </summary>
internal sealed partial class FakeSpeechWorker : IDisposable
{
    private WorkerProcess? _current;

    public ConcurrentQueue<string> Frames { get; } = new();
    public bool InitSucceeds { get; set; } = true;
    public string Transcript { get; set; } = "hello world";

    /// <summary>How many times a host started this worker.</summary>
    public int Starts;

    /// <summary>When set, the worker exits instead of answering a transcribe
    /// request — a crash mid-decode.</summary>
    public bool DieOnTranscribe { get; set; }

    // A restart may begin as soon as the old process signals its exit.
    public Action? BeforeCrashPipesClose { get; set; }

    /// <summary>When set, the worker answers a transcribe request with a
    /// refusal and stays up — an engine error, not a crash.</summary>
    public bool RefuseTranscribe { get; set; }

    /// <summary>When set, a transcribe request waits for it before answering.</summary>
    public ManualResetEventSlim? HoldTranscribe { get; set; }

    /// <summary>When set, an init request waits for it before answering — a
    /// model that takes a while to load.</summary>
    public ManualResetEventSlim? HoldInit { get; set; }

    // ─── The live-preview stream ops ────────────────────────────────────

    /// <summary>What every <c>stream_push</c> answers, the worker's notes
    /// rendering with its speaker tags.</summary>
    public string PreviewReply { get; set; } = "[You] hello world from preview";

    /// <summary>When set, <c>stream_start</c> is refused, as by a worker
    /// whose engine cannot open a live session.</summary>
    public bool RefuseStreamStart { get; set; }

    /// <summary>When set, a <c>stream_cancel</c> waits for it before
    /// answering — a cancel stuck in the worker.</summary>
    public ManualResetEventSlim? HoldStreamCancel { get; set; }

    /// <summary>When set, a <c>stream_start</c> waits for it before
    /// answering — a live session slow to open.</summary>
    public ManualResetEventSlim? HoldStreamStart { get; set; }

    /// <summary>When set, a <c>stream_push</c> waits for it before
    /// answering — a caption decode still on the wire. Bound before the
    /// frame is recorded, so a test that sees the frame knows it is held.
    /// Keep it under the host's 2 s limit on a stream op.</summary>
    public ManualResetEventSlim? HoldStreamPush { get; set; }

    private int _streams;

    /// <summary>The frames of <paramref name="op"/>, parsed, in order.</summary>
    public IReadOnlyList<JsonElement> FramesOf(string op) =>
        Frames.Select(f => JsonDocument.Parse(f).RootElement)
            .Where(f => f.GetProperty("op").GetString() == op)
            .ToArray();

    public bool IsAlive => _current?.IsAlive == true;

    // Match the native platform: each CreateWorker owns a distinct process.
    // Cancelling an old host must never close a replacement host's pipes.
    public IAsrWorkerProcess CreateProcess() => _current = new WorkerProcess(this);

    public void Crash() => _current?.Crash();
    public void Dispose() => _current?.Dispose();

    /// <summary>The ops requested, in order.</summary>
    public IReadOnlyList<string> Ops() =>
        Frames.Select(f => JsonDocument.Parse(f).RootElement.GetProperty("op").GetString() ?? "")
            .ToArray();

    private sealed class WorkerProcess(FakeSpeechWorker owner) : IAsrWorkerProcess
    {
        private AnonymousPipeServerStream? _hostToWorker;
        private AnonymousPipeClientStream? _workerIn;
        private AnonymousPipeServerStream? _workerToHost;
        private AnonymousPipeClientStream? _hostIn;
        private CancellationTokenSource? _cts;

        public bool IsAlive { get; private set; }

        public Stream StandardIn => _hostToWorker ?? throw new InvalidOperationException("not started");

        public Stream StandardOut => _hostIn ?? throw new InvalidOperationException("not started");

        public Task StartAsync(CancellationToken ct)
        {
            _hostToWorker = new AnonymousPipeServerStream(PipeDirection.Out);
            _workerIn = new AnonymousPipeClientStream(PipeDirection.In, _hostToWorker.ClientSafePipeHandle);
            _workerToHost = new AnonymousPipeServerStream(PipeDirection.Out);
            _hostIn = new AnonymousPipeClientStream(PipeDirection.In, _workerToHost.ClientSafePipeHandle);
            _cts = new CancellationTokenSource();
            IsAlive = true;
            Interlocked.Increment(ref owner.Starts);
            var token = _cts.Token;
            var input = _workerIn;
            var output = _workerToHost;
            _ = Task.Run(() => ServeAsync(input, output, token));
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
                    var json = Encoding.UTF8.GetString(payload);
                    ct.ThrowIfCancellationRequested();
                    // Bind this request before publishing it to a cancelling test.
                    var decodeHold = owner.HoldTranscribe;
                    var initHold = owner.HoldInit;
                    var pushHold = owner.HoldStreamPush;
                    owner.Frames.Enqueue(json);

                    var op = JsonDocument.Parse(json).RootElement.GetProperty("op").GetString();
                    if (op == "transcribe" && owner.DieOnTranscribe)
                    {
                        Crash();
                        return;
                    }

                    // A slow or silent answer (TakeFakes.cs); a host that gives up stops this.
                    await owner.DelayAsync(op, ct);

                    if (op == "init")
                    {
                        initHold?.Wait(TimeSpan.FromSeconds(10), ct);
                    }

                    if (op == "stream_cancel")
                    {
                        owner.HoldStreamCancel?.Wait(TimeSpan.FromSeconds(10), ct);
                    }

                    if (op == "stream_start")
                    {
                        owner.HoldStreamStart?.Wait(TimeSpan.FromSeconds(10), ct);
                    }

                    if (op == "stream_push")
                    {
                        pushHold?.Wait(TimeSpan.FromSeconds(10), ct);
                    }

                    object reply = op switch
                    {
                        "init" when owner.InitSucceeds => new { ok = true, on_npu = false },
                        "init" => new { ok = false, error = "no speech model generation" },
                        "transcribe" => Transcribe(decodeHold, ct),
                        "stream_start" when owner.RefuseStreamStart =>
                            new { ok = false, error = "no live session on this engine", engine = "whisper" },
                        "stream_start" => new
                        {
                            ok = true,
                            stream_id = $"preview-{Interlocked.Increment(ref owner._streams)}",
                            engine = "whisper",
                        },
                        "stream_push" => new { ok = true, text = owner.PreviewReply, is_endpoint = false },
                        "stream_finish" => new { ok = true, text = owner.PreviewReply },
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

        private object Transcribe(ManualResetEventSlim? hold, CancellationToken ct)
        {
            hold?.Wait(TimeSpan.FromSeconds(10), ct);
            return owner.RefuseTranscribe
                ? new { ok = false, error = "the engine could not decode that" }
                : new { ok = true, text = owner.Transcript };
        }

        /// <summary>The process exits on its own, as a crash would.</summary>
        public void Crash() => Dispose(owner.BeforeCrashPipesClose);

        public Task StopAsync(TimeSpan grace, CancellationToken ct)
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose() => Dispose(null);

        private void Dispose(Action? afterExit)
        {
            // Keep this process lifetime's handles before publishing its exit.
            // A host retry can call StartAsync while these old pipes close.
            var cancel = _cts;
            var hostToWorker = _hostToWorker;
            var workerToHost = _workerToHost;
            var workerIn = _workerIn;
            var hostIn = _hostIn;
            IsAlive = false;
            try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
            hostToWorker?.Dispose();
            afterExit?.Invoke();
            workerToHost?.Dispose();
            workerIn?.Dispose();
            hostIn?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeTarget : IFocusedApplicationResolver
{
    public FocusedApplicationResult Next { get; set; } =
        FocusedApplicationResult.Resolved("notepad", 42, "Untitled - Notepad");

    public FocusedApplicationResult Resolve() => Next;
}

internal sealed class FakePaste : IPasteKeystroke
{
    /// <summary>False: the target refused the keystroke.</summary>
    public bool Succeeds { get; set; } = true;

    /// <summary>When set, the paste keystroke blocks until it is signalled —
    /// a dictation held in the middle of its insertion.</summary>
    public ManualResetEventSlim? Hold { get; set; }
    public ManualResetEventSlim Entered { get; } = new();
    public int Count;

    /// <summary>Where a paste that succeeds lands, when anything is listening.</summary>
    public Action? Landed { get; set; }

    public bool TrySend()
    {
        Interlocked.Increment(ref Count);
        Entered.Set();
        Hold?.Wait(TimeSpan.FromSeconds(10));
        if (Succeeds)
        {
            Landed?.Invoke();
        }

        return Succeeds;
    }
}

internal sealed class FakeSecureField : ISecureFieldProbe
{
    public SecureFieldState Next { get; set; } = SecureFieldState.NotSecure;

    /// <summary>When set, a probe blocks until it is signalled — a target
    /// slow to answer UI Automation.</summary>
    public ManualResetEventSlim? Hold { get; set; }

    /// <summary>Set to make Probe throw, which must not stop a dictation.</summary>
    public bool Throws { get; set; }

    public int Calls;

    public int ThreadId;

    public SecureFieldState Probe()
    {
        Interlocked.Increment(ref Calls);
        ThreadId = Environment.CurrentManagedThreadId;
        Hold?.Wait(TimeSpan.FromSeconds(10));
        return Throws ? throw new InvalidOperationException("UIA is having a day") : Next;
    }
}

internal sealed class FakeClipboard : IClipboard, IClipboardTextSnapshot
{
    public string? Text { get; set; } = "the user's clipboard";
    public bool Accepts { get; set; } = true;
    public List<string> Writes { get; } = [];

    /// <summary>Set to make a write throw, with the words in its message, as
    /// a clipboard API that echoes its input might.</summary>
    public bool Throws { get; set; }

    public bool TrySet(string text)
    {
        if (Throws)
        {
            throw new InvalidOperationException($"the clipboard is having a day with '{text}'");
        }

        if (!Accepts)
        {
            return false;
        }

        Text = text;
        Writes.Add(text);
        return true;
    }

    public bool TryRead(out string? text)
    {
        text = Text;
        return true;
    }
}

internal sealed class FakeLaunchAtLogin : ILaunchAtLogin
{
    public bool IsEnabled { get; set; }

    /// <summary>The entry names this copy but Settings switched it off. A
    /// change that is written clears it, as WindowsLaunchAtLogin removes the
    /// flag.</summary>
    public bool SwitchedOffInSettings { get; set; }

    public bool Accepts { get; set; } = true;

    /// <summary>False: a write reports success and changes nothing, as one a
    /// policy undoes might.</summary>
    public bool Takes { get; set; } = true;

    /// <summary>Set to make a read throw, as a registry read that fails might.</summary>
    public bool ReadThrows { get; set; }

    public List<bool> Requests { get; } = [];

    public int Reads;

    bool ILaunchAtLogin.IsEnabled
    {
        get
        {
            Interlocked.Increment(ref Reads);
            return ReadThrows ? throw new UnauthorizedAccessException("the Run key is having a day") : IsEnabled;
        }
    }

    public bool TrySet(bool enabled)
    {
        Requests.Add(enabled);
        if (!Accepts)
        {
            return false;
        }

        if (Takes)
        {
            IsEnabled = enabled;
            SwitchedOffInSettings = false;
        }

        return true;
    }
}

internal sealed class FakeMicrophoneAccess : IMicrophoneAccessProbe
{
    public MicrophoneAccess Access { get; set; } = MicrophoneAccess.Allowed;

    /// <summary>Set to make Probe throw, as a registry read that fails might.</summary>
    public bool Throws { get; set; }

    /// <summary>Set to make Probe wait for it (10 s at most), as a slow
    /// registry walk would.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public int Probes;

    public MicrophoneAccess Probe()
    {
        Interlocked.Increment(ref Probes);
        Gate?.Wait(TimeSpan.FromSeconds(10));
        return Throws ? throw new InvalidOperationException("the consent store is having a day") : Access;
    }
}

internal sealed class FakePlatform : IDictationPlatform
{
    public List<FakeTrigger> Triggers { get; } = [];
    public List<FakeMicrophone> Microphones { get; } = [];
    public List<DictationWorkerPlan> Plans { get; } = [];
    public FakeSpeechWorker Worker { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public FakeSecureField SecureField { get; } = new();
    public FakeLaunchAtLogin Login { get; } = new();
    public List<string> OpenedFolders { get; } = [];
    public bool FolderOpens { get; set; } = true;

    /// <summary>Every file Show this copy in File Explorer asked for.</summary>
    public List<string> ShownFiles { get; } = [];

    /// <summary>False: File Explorer does not show it. Null: the call throws.</summary>
    public bool? FileShows { get; set; } = true;
    public FakeTarget Target { get; } = new();
    public FakePaste Paste { get; } = new();
    public FakeField Field { get; }
    public int SpanTargets;

    /// <summary>Runs while the field is being resolved, on the watch's thread.</summary>
    public Action? OnResolve { get; set; }

    public FakePlatform(ObserverTime? time = null)
    {
        Field = new FakeField(time ?? new ObserverTime(), () => Triggers.Count == 0 ? null : Trigger.KeyboardInputObserved);
        // A paste that lands puts what the clipboard held into the field.
        Paste.Landed = () => Field.Paste(Clipboard.Text ?? string.Empty);
    }
    public FakeMicrophoneAccess Access { get; } = new();
    public bool TriggerRegisters { get; set; } = true;

    /// <summary>Give every trigger the hook's real state machine
    /// (<see cref="FakeTrigger.UseGesture"/>), re-arms included.</summary>
    public bool Gestures { get; set; }

    public bool MicrophoneStarts { get; set; } = true;
    public Exception? WorkerFailure { get; set; }
    public int StartupFinishedCalls;
    public int CreateWorkerCalls;

    /// <summary>Runs when a worker's model load is over, before the start
    /// that loaded it decides anything — the last moment a stop can race it.</summary>
    public Action? OnStartupFinished { get; set; }

    public FakeTrigger Trigger => Triggers[^1];
    public FakeMicrophone Microphone => Microphones[^1];

    public IMicrophoneAccessProbe MicrophoneAccess => Access;

    public ILaunchAtLogin LaunchAtLogin => Login;

    public bool OpenFolder(string path)
    {
        OpenedFolders.Add(path);
        return FolderOpens;
    }

    public bool ShowInFolder(string file)
    {
        ShownFiles.Add(file);
        return FileShows ?? throw new System.ComponentModel.Win32Exception(2, @"C:\Users\someone\Dictation.exe");
    }

    public IDictationTrigger CreateTrigger(HotkeyBinding binding, bool cancelOnChord, bool handsFree)
    {
        var trigger = new FakeTrigger(binding, cancelOnChord, handsFree) { RegisterSucceeds = TriggerRegisters };
        if (Gestures)
        {
            trigger.UseGesture();
        }

        Triggers.Add(trigger);
        return trigger;
    }

    public IAudioCaptureDevice CreateMicrophone()
    {
        var microphone = new FakeMicrophone { StartSucceeds = MicrophoneStarts };
        Microphones.Add(microphone);
        return microphone;
    }

    public DictationWorker CreateWorker(DictationWorkerPlan plan)
    {
        if (WorkerFailure is not null)
        {
            throw WorkerFailure;
        }

        Plans.Add(plan);
        Interlocked.Increment(ref CreateWorkerCalls);
        return new DictationWorker(Worker.CreateProcess(), () =>
        {
            Interlocked.Increment(ref StartupFinishedCalls);
            OnStartupFinished?.Invoke();
        });
    }

    public DictationInsertionEdges CreateInsertionEdges() =>
        new(Target, Clipboard, Paste, SecureField);

    /// <summary>Copy last text's writer: the same fake the insertion uses,
    /// as both are the one Windows clipboard.</summary>
    IClipboard IDictationPlatform.Clipboard => Clipboard;

    /// <summary>The window in front, for the hook watch (HookWatchTests.cs).</summary>
    public FakeForeground Foreground { get; } = new();

    IForegroundElevation IDictationPlatform.ForegroundElevation => Foreground;

    /// <summary>How many times the countdown's sound was played.</summary>
    public int AlertSounds;

    /// <summary>The sound throws, as a broken audio stack might.</summary>
    public bool AlertSoundThrows { get; set; }

    public void PlayAlertSound()
    {
        AlertSounds++;
        if (AlertSoundThrows)
        {
            throw new InvalidOperationException("no audio device");
        }
    }

    public ISpanObserverTarget CreateSpanObserverTarget(SpanObserverKeyboardSignal keyboard, SpanObserverOptions options)
    {
        Interlocked.Increment(ref SpanTargets);
        OnResolve?.Invoke();
        Field.Keyboard = keyboard;
        return Field;
    }
}

internal sealed class RecordingView : IDictationView
{
    public List<DictationViewState> States { get; } = [];

    public DictationViewState Last => States[^1];

    public int QuitRequests;

    public int RevealRequests;

    /// <summary>Runs after each render is recorded.</summary>
    public Action<DictationViewState>? OnRender { get; set; }

    public void Render(DictationViewState state)
    {
        States.Add(state);
        OnRender?.Invoke(state);
    }

    /// <summary>Runs inside <see cref="Quit"/>, as the real window's close
    /// does: closing it disposes the controller there and then.</summary>
    public Action? OnQuit { get; set; }

    public void Quit()
    {
        Interlocked.Increment(ref QuitRequests);
        OnQuit?.Invoke();
    }

    public void Reveal() => Interlocked.Increment(ref RevealRequests);
}

/// <summary>The floating indicator, as a list of what it was told to draw.</summary>
internal sealed class FakeHud : IDictationHud
{
    /// <summary>The hold a feed built in a test runs with: the default key
    /// and the product's hold policy, as the rig's controller has.</summary>
    public static readonly DictationHudHold Hold = new("Right Ctrl", DictationHoldPolicy.Windows);

    /// <summary>Every pill shown, in order.</summary>
    public List<DictationHudModel> Shows { get; } = [];

    /// <summary>Whether each pill in <see cref="Shows"/> was to be spoken.</summary>
    public List<bool> Announced { get; } = [];

    /// <summary>Every level the bars were told to draw, in order.</summary>
    public List<float> Levels { get; } = [];

    /// <summary>How many level draws were attempted, thrown ones included.</summary>
    public int LevelAttempts;

    /// <summary>The slot holds the level bars, not the tone dot: a drawn
    /// level on a pill on screen puts them in, and every show and hide puts
    /// the dot back, as the window does (pinned on its source by
    /// <see cref="DictationHudLevelWindowTests"/>).</summary>
    public bool Bars { get; private set; }

    public int Hides;

    /// <summary>What is on screen; null when nothing is.</summary>
    public DictationHudModel? Current { get; private set; }

    /// <summary>Set to make every draw throw, as a broken window might.</summary>
    public bool Throws { get; set; }

    /// <summary>Set to make asking the window whether it is showing throw,
    /// as a window torn down under the heartbeat might.</summary>
    public bool IsShowingThrows { get; set; }

    /// <summary>The window's own account (<see cref="IDictationHud.IsShowing"/>):
    /// a pill is on screen.</summary>
    public bool IsShowing => IsShowingThrows
        ? throw new InvalidOperationException("the indicator window cannot say")
        : Current is not null;

    /// <summary>The window loses its pill without the feed knowing, as the
    /// Mac's panel once sat off screen for 34 hours.</summary>
    public void Vanish()
    {
        Current = null;
        Bars = false;
    }

    /// <summary>The window shows <paramref name="model"/> without the feed
    /// having asked: a pill the feed took down that stayed up.</summary>
    public void Stray(DictationHudModel model) => Current = model;

    /// <summary>Whether the microphone is open, read as each sentence is
    /// spoken: the rig's fake microphone.</summary>
    public Func<bool> MicrophoneOpen { get; set; } = () => false;

    /// <summary>
    /// What a screen reader is asked to say, as the window decides it
    /// (<c>Announce</c> and <c>NameQuietly</c> in DictationHudWindow, pinned on
    /// its source by <see cref="IndicatorAnnouncementTests"/>): one live-region
    /// change and one UI Automation notification for an announced pill whose
    /// sentence is not the last one named; an unannounced pill names its quiet
    /// name; a hide forgets. Each with whether the microphone was open then.
    /// </summary>
    public List<(string Sentence, bool MicrophoneOpen)> Spoken { get; } = [];

    private string _lastNamed = string.Empty;

    public void Show(DictationHudModel model, bool announce)
    {
        if (Throws)
        {
            throw new InvalidOperationException("the indicator window is having a day");
        }

        Shows.Add(model);
        Announced.Add(announce);
        Current = model;
        Bars = false;
        if (!model.IsVisible)
        {
            _lastNamed = string.Empty;
        }
        else if (!announce)
        {
            _lastNamed = model.QuietName;
        }
        else if (model.ShouldAnnounce && LiveRegionAnnouncement.ShouldRaise(_lastNamed, model.AnnouncedText))
        {
            _lastNamed = model.AnnouncedText;
            Spoken.Add((model.AnnouncedText, MicrophoneOpen()));
        }
    }

    public void Level(float level)
    {
        LevelAttempts++;
        if (Throws)
        {
            throw new InvalidOperationException("the indicator window is having a day");
        }

        Levels.Add(level);
        Bars = Current is not null;
    }

    public void Hide()
    {
        if (Throws)
        {
            throw new InvalidOperationException("the indicator window is having a day");
        }

        Hides++;
        Current = null;
        Bars = false;
        _lastNamed = string.Empty;
    }
}

/// <summary>
/// The correction watch's wall clock. It moves only when the observer waits,
/// by exactly the wait, so a 90-second watch runs in microseconds — until it
/// reaches <see cref="Limit"/>, where it parks until the limit moves or the
/// watch is cancelled.
/// </summary>
internal sealed class ObserverTime : TimeProvider
{
    private readonly Lock _gate = new();
    private long _now = 1_790_000_000_000;
    private long _limit = long.MaxValue;
    private bool _parked;
    private (long At, Action Run)? _when;

    public long NowMs
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    /// <summary>The clock does not pass this; <c>long.MaxValue</c> by default.</summary>
    public long Limit
    {
        get
        {
            lock (_gate)
            {
                return _limit;
            }
        }
        set
        {
            lock (_gate)
            {
                _limit = value;
            }
        }
    }

    /// <summary>A wait is parked at <see cref="Limit"/>.</summary>
    public bool Parked
    {
        get
        {
            lock (_gate)
            {
                return _parked;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        Action? run = null;
        long now;
        lock (_gate)
        {
            now = _now;
            if (_when is { } when && now >= when.At)
            {
                run = when.Run;
                _when = null;
            }
        }

        run?.Invoke();
        return DateTimeOffset.FromUnixTimeMilliseconds(now);
    }

    /// <summary>Run <paramref name="action"/> once, on the first read of the
    /// clock at or after <paramref name="atMs"/> — inside the watch, on its thread.</summary>
    public void When(long atMs, Action action)
    {
        lock (_gate)
        {
            _when = (atMs, action);
        }
    }

    /// <summary>The observer's delay.</summary>
    public async Task Delay(int milliseconds, CancellationToken cancel)
    {
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_now + milliseconds <= _limit)
                {
                    _now += milliseconds;
                    _parked = false;
                    return;
                }

                _parked = true;
            }

            await Task.Delay(2, cancel);
        }
    }

    /// <summary>Let the clock run to its end.</summary>
    public void Release() => Limit = long.MaxValue;

    /// <summary>Wait, in real time, until the watch is parked at the limit.</summary>
    public void WaitUntilParked()
    {
        if (!SpinWait.SpinUntil(() => Parked, TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("the correction watch never reached the limit");
        }
    }
}

/// <summary>
/// The field a paste lands in, as UI Automation shows it to the span
/// observer: one text model with a caret, around sentinel text that must never
/// leave it. The user's fixes are scheduled on the observer's clock after each
/// paste, and a typed one reports its key-down through the hook's feed, as a
/// real keystroke would.
/// </summary>
internal sealed class FakeField(ObserverTime time, Func<Action<long, int>?> hook) : ISpanObserverTarget
{
    public const string SentinelBefore = "SENTINEL-7Q2-BEFORE";
    public const string SentinelAfter = "SENTINEL-7Q2-AFTER";

    private readonly Lock _gate = new();
    private string _text = string.Empty;
    private int _caret;
    private long _pastedAt = -1;
    private bool _fixed;

    /// <summary>What surrounds the paste: other text in the user's document.</summary>
    public string Before { get; set; } = $"Notes {SentinelBefore} for the review on Tuesday. ";

    public string After { get; set; } = $" Then call {SentinelAfter} about the invoice.";

    /// <summary>Notepad, in the process the paste went into (FakeTarget's 42).</summary>
    public SpanObserverTargetIdentity Identity { get; set; } = new("notepad", 42, "Edit", 7, false);

    public SecureFieldState Secure { get; set; } = SecureFieldState.NotSecure;

    /// <summary>What the watch passed in; the field reports its last key-down.</summary>
    public SpanObserverKeyboardSignal? Keyboard { get; set; }

    public SpanObserverKeyboardInput? LastKeyboardInput => Keyboard?.Last;

    public int Reads;

    /// <summary>The fix the user makes after every paste, or null.</summary>
    public (string From, string To, long AfterMs, bool Typed)? Fix { get; set; }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text;
            }
        }
    }

    public void Paste(string text)
    {
        lock (_gate)
        {
            _text = Before + text + After;
            _caret = Before.Length + text.Length;
            _pastedAt = time.NowMs;
            _fixed = false;
        }
    }

    /// <summary>After each paste: at <paramref name="afterMs"/> on the
    /// observer's clock, <paramref name="from"/> becomes <paramref name="to"/>.</summary>
    public void FixAfterEachPaste(string from, string to, long afterMs = 2_000, bool typed = true) =>
        Fix = (from, to, afterMs, typed);

    public SpanObserverResult<SecureFieldState> ProbeSecureField() =>
        SpanObserverResult<SecureFieldState>.Success(Secure);

    public SpanObserverResult<SpanObserverSelection> GetSelection()
    {
        lock (_gate)
        {
            Apply();
            return SpanObserverResult<SpanObserverSelection>.Success(new SpanObserverSelection(_caret, 0));
        }
    }

    public SpanObserverResult<string> ReadRange(int start, int length)
    {
        Interlocked.Increment(ref Reads);
        lock (_gate)
        {
            Apply();
            var s = Math.Clamp(start, 0, _text.Length);
            var e = Math.Clamp(start + length, s, _text.Length);
            return SpanObserverResult<string>.Success(_text[s..e]);
        }
    }

    public SpanObserverResult<string> ReadText(int maxChars)
    {
        Interlocked.Increment(ref Reads);
        lock (_gate)
        {
            Apply();
            return SpanObserverResult<string>.Success(_text.Length <= maxChars ? _text : _text[..maxChars]);
        }
    }

    /// <summary>No change events: the observer polls, as it does for many
    /// real providers.</summary>
    public IDisposable? Subscribe(Action<SpanObserverSignal> handler) => null;

    /// <summary>The scheduled fix, once its moment has come.</summary>
    private void Apply()
    {
        if (_fixed || Fix is not { } fix || _pastedAt < 0 || time.NowMs < _pastedAt + fix.AfterMs)
        {
            return;
        }

        _fixed = true;
        var at = _pastedAt + fix.AfterMs;
        if (fix.Typed)
        {
            hook()?.Invoke(at, Identity.ProcessId);
        }

        var index = _text.IndexOf(fix.From, _caret > 0 ? Before.Length : 0, StringComparison.Ordinal);
        if (index >= 0)
        {
            _text = _text[..index] + fix.To + _text[(index + fix.From.Length)..];
            _caret += fix.To.Length - fix.From.Length;
        }
    }
}

/// <summary>The controller's monotonic clock, moved by hand.</summary>
internal sealed class FakeClock
{
    public TimeSpan Now { get; set; } = TimeSpan.FromHours(1);

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// The WPF dispatcher's <c>InvokeAsync</c>, as a queue the test drains: a
/// posted callback runs AFTER whatever the UI thread is doing now.
/// </summary>
internal sealed class DeferredDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private int _posted;

    public void Post(Action action)
    {
        // The callback first, then the count: a test that sees the count go
        // up (RecentResultTests.Answer) then drains, and must find it queued.
        _queue.Enqueue(action);
        Interlocked.Increment(ref _posted);
    }

    /// <summary>How many callbacks were ever posted, drained or not. Each one
    /// counted is already in the queue.</summary>
    public int Posted => Volatile.Read(ref _posted);

    public int Pending => _queue.Count;

    public void Drain()
    {
        while (_queue.TryDequeue(out var action))
        {
            action();
        }
    }
}

/// <summary>An installed copy on disk: python, worker script and a complete
/// CTranslate2 model directory under <c>resources\</c>.</summary>
internal sealed class InstalledCopy : IDisposable
{
    public InstalledCopy()
    {
        Root = Directory.CreateTempSubdirectory("dictation-app-").FullName;
        Write("resources/python/python.exe");
        Write("resources/runtime/transcription_worker/asr_server.py");
        foreach (var file in new[] { "model.bin", "config.json", "tokenizer.json", "preprocessor_config.json" })
        {
            Write($"resources/model/{file}");
        }
    }

    public string Root { get; }

    public string ModelDirectory => Path.Combine(Root, "resources", "model");

    public void Write(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    public void Delete(string relative) => File.Delete(Path.Combine(Root, relative));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

/// <summary>One controller over the fakes, and the handles a test drives.</summary>
internal sealed class Rig : IDisposable
{
    /// <param name="learning">Compose learning, as the app does: a
    /// <c>learning.json</c> under this copy's data folder, judged against the
    /// shared golden word list. Off by default, so the tests that are not
    /// about it never start a correction watch.</param>
    /// <param name="hud">Compose the floating indicator, <see cref="Hud"/>.
    /// On by default, so every controller test also runs the feed.</param>
    public Rig(
        Func<string, string?>? environment = null,
        Func<DictationControllerOptions, DictationControllerOptions>? options = null,
        Microsoft.Extensions.Logging.ILoggerFactory? logs = null,
        Action<Rig>? beforeStart = null,
        bool atLogin = false,
        bool learning = false,
        bool hud = true)
    {
        Platform = new FakePlatform(Time);
        Hud.MicrophoneOpen = () => Platform.Microphones.Any(m => m.Open);
        Settings = new DictationSettingsStore(
            Path.Combine(DataDirectory, "settings.json"),
            NullLogger<DictationSettingsStore>.Instance);
        if (learning)
        {
            Learning = new DictationLearning(
                new LearningStore(LearningDirectory),
                new WordListLexicon(LearningGoldenTests.SharedLexicon),
                Time,
                logs?.CreateLogger<DictationLearning>());
        }

        beforeStart?.Invoke(this);
        Controller = new DictationController(
            Platform,
            View,
            Dispatcher.Post,
            logs ?? NullLoggerFactory.Instance,
            Settings,
            environment ?? (_ => null),
            Copy.Root,
            (options ?? (o => o))(Options()) with { LogDirectory = LogDirectory },
            Learning,
            hud ? Hud : null);
        if (Controller.Corrections is { } watch)
        {
            // Begin raises Started on the caller's thread just before it
            // starts the watch on the pool, so this reads the feed at the
            // last moment the watch has not yet read anything: a feed
            // attached after Begin is caught every time, not only when the
            // pool wins the race.
            watch.Started += (_, _) =>
            {
                var feed = Platform.Triggers.Count == 0 ? null : Platform.Trigger.KeyboardInputObserved;
                if (!Equals(feed, (Action<long, int>)watch.Keyboard.Record))
                {
                    Interlocked.Increment(ref _watchesBegunWithoutFeed);
                }
            };
        }

        Startup = Controller.Start(atLogin);
    }

    private int _watchesBegunWithoutFeed;

    /// <summary>Correction watches that began before the hook's key-down
    /// feed was theirs: each could judge the user's first typed fix untyped.</summary>
    public int WatchesBegunWithoutFeed => Volatile.Read(ref _watchesBegunWithoutFeed);

    /// <summary>The product's options with the test clock, no paste settle,
    /// no restart back-off and a synchronous field check. The hold policy is
    /// the product's, silence stops included: a test that waits long while a
    /// hold listens, for something else, says so with
    /// <see cref="DictationHoldPolicy.WithoutSilenceStops"/>. A test changes
    /// them through the constructor's <c>options</c>.</summary>
    private DictationControllerOptions Options() => new()
    {
        PasteSettleDelay = TimeSpan.Zero,
        WorkerRestartDelay = TimeSpan.Zero,
        Clock = () => Clock.Now,
        RunFieldCheck = check => Task.FromResult(check()),
        RunClipboardCopy = write => Task.FromResult(write()),
        RunMicrophoneProbe = read => Task.FromResult(read()),
        Now = () => Now,
        Time = Time,
        ObserverDelay = Time.Delay,
        // The spool in this copy's data folder, written at once; the watchdog
        // and the worker's health check off, as these tests move the clock
        // with no audio (TakeTests.cs turn them on).
        Takes = TakeFiles.Options(RecoveryDirectory),
    };

    public FakeClock Clock { get; } = new();
    public ObserverTime Time { get; } = new();

    /// <summary>The wall clock a recent result is stamped with.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 15, 41, 0, TimeSpan.Zero);
    public DictationStartup Startup { get; }
    public string LogDirectory => Path.Combine(Copy.Root, "logs");
    public string DataDirectory => Path.Combine(Copy.Root, "data");
    public string LearningDirectory => Path.Combine(DataDirectory, LearningStore.DirectoryName);
    public string LearningFile => Path.Combine(LearningDirectory, LearningStore.FileName);
    public string RecoveryDirectory => Path.Combine(DataDirectory, "recovery");
    public DictationLearning? Learning { get; }

    public InstalledCopy Copy { get; } = new();
    public DictationSettingsStore Settings { get; }
    public FakePlatform Platform { get; }
    public RecordingView View { get; } = new();
    public FakeHud Hud { get; } = new();
    public DeferredDispatcher Dispatcher { get; } = new();
    public DictationController Controller { get; }

    public async Task EnableAsync()
    {
        await Controller.EnableAsync();
        Dispatcher.Drain();
    }

    /// <summary>The user presses the hold key and the next poll sees it.</summary>
    public void Press()
    {
        Platform.Trigger.Down = true;
        Controller.Tick();
        Platform.Microphone.Speak();
    }

    /// <summary>A press, then a release, then everything that follows until
    /// the window names the outcome.</summary>
    public void Dictate(Func<DictationViewState, bool> done)
    {
        Press();
        Release();
        DrainUntil(() => done(View.Last));
    }

    /// <summary>The user lets go and the next poll sees it.</summary>
    public void Release()
    {
        Platform.Trigger.Down = false;
        Controller.Tick();
    }

    /// <summary>Keep recognition pending until Release returns, so even a
    /// buffered worker reply cannot block the test itself inside the held paste.</summary>
    public void ReleaseToHeldPaste()
    {
        Assert.NotNull(Platform.Paste.Hold);
        using var decode = new ManualResetEventSlim();
        Platform.Paste.Entered.Reset();
        Platform.Worker.HoldTranscribe = decode;
        try
        {
            Release();
            decode.Set();
            Assert.True(Platform.Paste.Entered.Wait(TimeSpan.FromSeconds(10)), "the paste never started");
        }
        finally
        {
            decode.Set();
            Platform.Worker.HoldTranscribe = null;
        }
    }

    /// <summary>Poll the hold key and drain posted events until
    /// <paramref name="done"/> holds — background work (a worker restart)
    /// reports back through the dispatcher and is noticed by the tick.</summary>
    public void TickUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            Controller.Tick();
            Dispatcher.Drain();
            if (done())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the controller never reached the expected state");
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>Drain posted events until <paramref name="done"/> holds.</summary>
    public void DrainUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            Dispatcher.Drain();
            if (done())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the controller never reached the expected state");
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>Wait for the correction watch the last dictation started, if any.</summary>
    public void WaitForWatch()
    {
        Assert.True(
            WatchesBegunWithoutFeed == 0,
            $"{WatchesBegunWithoutFeed} correction watch(es) began before the hook's key-down feed was attached");
        var watch = Controller.Corrections?.Completion ?? Task.CompletedTask;
        if (!watch.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("the correction watch never ended");
        }
    }

    public void Dispose()
    {
        Controller.Dispose();
        Controller.Corrections?.Completion.Wait(TimeSpan.FromSeconds(10));
        Platform.Worker.Dispose();
        Copy.Dispose();
    }
}
