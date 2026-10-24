using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

/// <summary>The memory tests run alone: they read the process's live memory,
/// which a test running beside them would move.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TapMemoryCollection
{
    public const string Name = "Tap memory";
}

/// <summary>
/// The microphone tap's memory split (LIMIT-WIN-B, step 1 of the limit's
/// design): a hold is kept at 16 kHz PCM16 as it arrives, 32 KB a second,
/// whatever the device's rate; the memory cap is the tap's own bound, apart
/// from the recording limit; and a hold that reaches it says so once.
/// </summary>
[Collection(TapMemoryCollection.Name)]
public sealed class MicrophoneTapMemoryTests
{
    private readonly Hotkey _hotkey = new();
    private readonly FakeMicrophone _microphone = new();
    private readonly List<MicrophoneTapResult> _results = [];

    private PushToTalkMicrophoneTap Tap(TimeSpan bufferLimit)
    {
        var tap = new PushToTalkMicrophoneTap(
            _hotkey, _microphone, new MicrophoneTapOptions(TimeSpan.Zero, bufferLimit), NullLogger<PushToTalkMicrophoneTap>.Instance);
        tap.CaptureCompleted += (_, result) => _results.Add(result);
        return tap;
    }

    /// <summary><paramref name="seconds"/> of a 440 Hz tone at
    /// <paramref name="rate"/>, as 10 ms buffers the way WASAPI delivers
    /// them, through the microphone. One buffer is reused: the tap copies.</summary>
    private void Deliver(double seconds, int rate = 48_000, int channels = 1)
    {
        var frames = rate / 100;
        var buffer = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                buffer[(i * channels) + c] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
            }
        }

        var chunk = new AudioChunk(buffer, rate, channels);
        for (var n = 0; n < (int)Math.Round(seconds * 100); n++)
        {
            _microphone.Emit(chunk);
        }
    }

    // ─── 16 kHz as it arrives ───────────────────────────────────────────

    /// <summary>
    /// The design's "the buffer for 20 min of synthetic audio is at most
    /// 40 MB": twenty minutes of a 48 kHz device costs 38.4 MB held, where
    /// the float list it replaces held 230 MB, and the process's own memory
    /// says the same as the tap's count. The release makes the one array the
    /// worker is sent, at 16 kHz.
    /// </summary>
    [Fact]
    public void Twenty_minutes_of_audio_is_kept_in_at_most_40_MB()
    {
        using var tap = Tap(TimeSpan.FromMinutes(21));
        _hotkey.Press();
        var before = LiveManagedBytes();

        Deliver(20 * 60);

        var retained = tap.RetainedAudioBytes;
        var grown = LiveManagedBytes() - before;
        Assert.InRange(retained, 38_000_000, 40_000_000);
        Assert.True(grown < 60_000_000, $"the process grew {grown / 1_000_000} MB holding twenty minutes");

        _hotkey.Release();
        var audio = Assert.Single(_results).Audio!;
        Assert.Equal(20 * 60 * WorkflowAudio.SampleRateHz, audio.Pcm.Length);
        Assert.Equal(TimeSpan.FromMinutes(20), audio.Duration);
        Assert.Equal(0, tap.RetainedAudioBytes);
    }

    private static long LiveManagedBytes()
    {
        // Segment GC double-counts promoted small objects in GetTotalMemory
        // on affected runtimes: https://github.com/dotnet/runtime/issues/134755.
        // Read the completed full-GC heap minus free space instead. Keep the
        // same live-memory bound; this also counts objects outside the tap.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var memory = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return memory.HeapSizeBytes - memory.FragmentedBytes;
    }

    /// <summary>
    /// What a hold keeps is, sample for sample, what the release-time
    /// conversion made from the whole hold, and so are its readings: the
    /// loudest frame and the voiced seconds the session gates and logs on.
    /// Every rate a device reports, mono and stereo, buffers of odd sizes.
    /// </summary>
    [Theory]
    [InlineData(48_000, 1)]
    [InlineData(48_000, 2)]
    [InlineData(44_100, 2)]
    [InlineData(22_050, 1)]
    [InlineData(16_000, 1)]
    [InlineData(8_000, 1)]
    public void What_a_hold_keeps_is_what_the_release_used_to_make_from_it(int rate, int channels)
    {
        using var tap = Tap(TimeSpan.FromMinutes(1));
        var random = new Random(rate + channels);
        var source = new List<float>();
        _hotkey.Press();
        for (var n = 0; n < 60; n++)
        {
            // Speech-like: loud and quiet stretches, some samples past full scale.
            var frames = random.Next(1, rate / 25);
            var amplitude = n % 3 == 0 ? 0.002f : 1.2f;
            var buffer = new float[frames * channels];
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = amplitude * ((2 * random.NextSingle()) - 1);
            }

            source.AddRange(WorkflowAudio.ToMono(buffer, channels));
            _microphone.Emit(new AudioChunk(buffer, rate, channels));
        }

        _hotkey.Release();

        var mono = source.ToArray();
        var audio = Assert.Single(_results).Audio!;
        Assert.Equal(WorkflowAudio.ToPcm16(WorkflowAudio.Resample(mono, rate)), audio.Pcm);
        Assert.Equal(WorkflowAudio.FrameRmsPeak(mono, rate), audio.FrameRmsPeak);
        Assert.Equal(WorkflowAudio.VoicedSeconds(mono, rate), audio.VoicedSeconds);
    }

    /// <summary>A hold shorter than one 30 ms frame reads its whole RMS, as
    /// the release-time reading did.</summary>
    [Fact]
    public void A_hold_shorter_than_a_frame_reads_its_whole_level()
    {
        using var tap = Tap(TimeSpan.FromMinutes(1));
        var buffer = Enumerable.Range(0, 400).Select(i => 0.01f * (i % 7)).ToArray();
        _hotkey.Press();
        _microphone.Emit(new AudioChunk(buffer, 48_000, 1));
        _hotkey.Release();

        var audio = Assert.Single(_results).Audio!;
        Assert.Equal(WorkflowAudio.FrameRmsPeak(buffer, 48_000), audio.FrameRmsPeak);
        Assert.Equal(0f, audio.VoicedSeconds);
        Assert.Equal(WorkflowAudio.ToPcm16(WorkflowAudio.Resample(buffer, 48_000)), audio.Pcm);
    }

    // ─── The memory cap ─────────────────────────────────────────────────

    /// <summary>At the cap the tap keeps what it has, drops the rest, and
    /// says so once, off the capture thread; the next hold starts empty. The
    /// cap falls 5 ms into a 10 ms buffer, which is cut there.</summary>
    [Fact]
    public void At_the_memory_cap_the_tail_is_dropped_and_the_cap_is_said_once()
    {
        using var tap = Tap(TimeSpan.FromSeconds(2.005));
        var raised = 0;
        using var said = new ManualResetEventSlim();
        tap.BufferLimitReached += (_, _) =>
        {
            Interlocked.Increment(ref raised);
            said.Set();
        };
        _hotkey.Press();

        Deliver(3.5);

        Assert.True(said.Wait(TimeSpan.FromSeconds(10)), "the cap was never said");
        Thread.Sleep(50);
        Assert.Equal(1, Volatile.Read(ref raised));
        _hotkey.Release();
        Assert.Equal(TimeSpan.FromSeconds(2.005), Assert.Single(_results).Audio!.Duration);

        _hotkey.Press();
        Deliver(1);
        _hotkey.Release();
        Assert.Equal(TimeSpan.FromSeconds(1), _results[^1].Audio!.Duration);
        Assert.Equal(1, Volatile.Read(ref raised));
    }

    /// <summary>
    /// The design's "changing the experience limit does not change the
    /// memory cap": the policy's limit and its memory cap are two numbers,
    /// a minute apart in the product, and a limit at or past the cap is
    /// refused rather than quietly dropping the words past the cap.
    /// </summary>
    [Fact]
    public void Changing_the_recording_limit_does_not_move_the_memory_cap()
    {
        var product = DictationHoldPolicy.Windows;
        Assert.Equal(product.Ceiling + TimeSpan.FromMinutes(1), product.MemoryCap);
        Assert.Equal(product.MemoryCap, product.TapOptions.BufferLimit);

        var shorter = product with { Ceiling = TimeSpan.FromMinutes(2) };
        Assert.Equal(TimeSpan.FromMinutes(6), shorter.Checked().TapOptions.BufferLimit);

        var past = product with { Ceiling = TimeSpan.FromMinutes(10) };
        Assert.Equal(TimeSpan.FromMinutes(6), past.TapOptions.BufferLimit);
        Assert.Throws<ArgumentException>(() => past.Checked());
        Assert.Throws<ArgumentException>(() => (product with { Ceiling = product.MemoryCap }).Checked());
        Assert.Equal(product, product.Checked());
    }

    /// <summary>A hotkey the test presses and releases by hand.</summary>
    private sealed class Hotkey : IPushToTalkHotkey
    {
        public event EventHandler? Pressed;

        public event EventHandler? Released;

        public bool IsHeld { get; private set; }

        public bool IsRegistered => true;

        public bool Start() => true;

        public void NotifyHotkeyMessage()
        {
        }

        public void PollForRelease()
        {
        }

        public void Press()
        {
            IsHeld = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        public void Release()
        {
            IsHeld = false;
            Released?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }
}
