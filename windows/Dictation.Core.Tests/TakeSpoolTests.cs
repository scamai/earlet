using System.Buffers.Binary;
using Dictation.Speech;

namespace Dictation.Core.Tests;

// LIMIT-WIN-A, the spool (LIMIT-DESIGN §5 step 3, "Take spool and Recover
// Last Dictation"): every take written as 16 kHz PCM16 while it is recorded,
// deleted after its words went in, kept after a failure, and an orphan left
// by a closing Dictation found at the next launch. The Mac's own spool comes
// in its stage 1; these are the design's scenarios, named for what they pin.

/// <summary>The conversion a take is spooled through (<see cref="DictationTakeConverter"/>).</summary>
public sealed class TakeConverterTests
{
    private static float[] Tone(int rate, int channels, double seconds)
    {
        var frames = (int)(rate * seconds);
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var value = 0.4f * MathF.Sin(2 * MathF.PI * 440 * i / rate) + 0.1f * MathF.Sin(2 * MathF.PI * 1_300 * i / rate);
            for (var c = 0; c < channels; c++)
            {
                samples[(i * channels) + c] = c == 1 ? value * 0.5f : value;
            }
        }

        return samples;
    }

    [Theory]
    [InlineData(48_000, 2)]
    [InlineData(44_100, 1)]
    [InlineData(44_100, 2)]
    [InlineData(32_000, 1)]
    public void A_take_converted_buffer_by_buffer_is_the_take_converted_at_once(int rate, int channels)
    {
        // Buffers of uneven sizes, as WASAPI hands them over.
        var interleaved = Tone(rate, channels, 1.3);
        var converter = new DictationTakeConverter();
        var streamed = new List<short>();
        var random = new Random(7);
        for (var at = 0; at < interleaved.Length;)
        {
            var frames = random.Next(1, 900);
            var length = Math.Min(frames * channels, interleaved.Length - at);
            streamed.AddRange(converter.Convert(new AudioChunk(interleaved.AsSpan(at, length).ToArray(), rate, channels)));
            at += length;
        }

        var once = WorkflowAudio.ToPcm16(WorkflowAudio.Resample(WorkflowAudio.ToMono(interleaved, channels), rate));
        // The one-shot conversion clamps its last output's right neighbour to
        // the last sample; the stream waits for a neighbour that never comes.
        Assert.InRange(once.Length - streamed.Count, 0, 1);
        for (var i = 0; i < streamed.Count; i++)
        {
            Assert.True(Math.Abs(once[i] - streamed[i]) <= 1, $"sample {i}: {once[i]} against {streamed[i]}");
        }

        Assert.Equal(streamed.Count, converter.Samples);
        Assert.Equal(TimeSpan.FromSeconds(streamed.Count / 16_000.0), converter.Duration);
    }

    [Fact]
    public void Sixteen_kilohertz_mono_is_copied_exactly()
    {
        var samples = Tone(16_000, 1, 0.25);
        var converter = new DictationTakeConverter();

        var pcm = converter.Convert(new AudioChunk(samples, 16_000, 1));

        Assert.Equal(WorkflowAudio.ToPcm16(samples), pcm);
    }

    [Fact]
    public void A_buffer_at_another_rate_mid_take_is_dropped_as_the_tap_drops_it()
    {
        var converter = new DictationTakeConverter();
        converter.Convert(new AudioChunk(Tone(48_000, 1, 0.1), 48_000, 1));
        var before = converter.Samples;

        Assert.Empty(converter.Convert(new AudioChunk(Tone(44_100, 1, 0.1), 44_100, 1)));
        Assert.Equal(before, converter.Samples);
        Assert.Equal(1, converter.Dropped);
    }

    [Fact]
    public void An_empty_or_malformed_buffer_converts_to_nothing()
    {
        var converter = new DictationTakeConverter();
        Assert.Empty(converter.Convert(new AudioChunk([], 48_000, 2)));
        Assert.Empty(converter.Convert(new AudioChunk([0.1f], 0, 1)));
        Assert.Empty(converter.Convert(new AudioChunk([0.1f], 48_000, 0)));
        Assert.Equal(0, converter.Samples);
    }
}

/// <summary>The spool's files (<see cref="DictationTakeSpool"/>).</summary>
public sealed class TakeSpoolTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 40, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dictation-spool-" + Guid.NewGuid().ToString("N"));

    private DictationTakeSpool Spool() => new(_directory, TakeFiles.Now);

    private static AudioChunk Speech(double seconds, int rate = 48_000)
    {
        var samples = new float[(int)(rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        }

        return new AudioChunk(samples, rate, 1);
    }

    private string[] Files() =>
        Directory.Exists(_directory)
            ? [.. Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
            : [];

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void The_spool_is_written_during_a_take_at_sixteen_kilohertz_and_flushed_with_every_buffer()
    {
        var spool = Spool();
        var take = spool.Begin(Now);
        spool.Append(take, Speech(0.5));

        // Written and flushed while the take still records: what a crash
        // now would leave on disk.
        var file = Assert.Single(Directory.GetFiles(_directory));
        Assert.StartsWith(DictationTakeSpool.TakePrefix, Path.GetFileName(file), StringComparison.Ordinal);
        var header = DictationTakeSpool.ReadHeader(file);
        Assert.Equal((Now, 8_000L), header);
        Assert.Equal(DictationTakeSpool.HeaderBytes + (8_000 * 2), new FileInfo(file).Length);
        Assert.Equal(TimeSpan.FromSeconds(0.5), take.Captured);
        spool.Leave(take);
    }

    [Fact]
    public void The_header_is_the_format_this_build_reads()
    {
        var header = DictationTakeSpool.Header(Now);

        Assert.Equal(DictationTakeSpool.HeaderBytes, header.Length);
        Assert.Equal("DICTTAKE"u8.ToArray(), header[..8]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)));
        Assert.Equal(Now.UtcTicks, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)));
    }

    [Fact]
    public void The_spool_is_deleted_after_its_words_went_in()
    {
        var spool = Spool();
        var take = spool.Begin(Now);
        spool.Append(take, Speech(0.5));

        spool.Discard(take);

        Assert.Empty(Files());
    }

    [Fact]
    public async Task The_spool_is_kept_after_a_failure_and_reads_back_as_the_take()
    {
        var spool = Spool();
        var take = spool.Begin(Now);
        spool.Append(take, Speech(0.25, 16_000));
        spool.Append(take, Speech(0.25, 16_000));

        Assert.True(await spool.Keep(take));

        Assert.Equal([DictationTakeSpool.KeptFileName], Files());
        var read = await spool.ReadKept();
        Assert.False(read.Unavailable);
        var audio = read.Audio;
        Assert.NotNull(audio);
        Assert.Equal(16_000, audio!.SampleRateHz);
        Assert.Equal(1, audio.Channels);
        Assert.Equal(TimeSpan.FromSeconds(0.5), audio.Duration);
        var tone = WorkflowAudio.ToPcm16(Speech(0.25, 16_000).Samples);
        Assert.Equal([.. tone, .. tone], audio.Pcm);
    }

    [Fact]
    public async Task One_take_is_kept_and_a_newer_failure_replaces_it()
    {
        var spool = Spool();
        var first = spool.Begin(Now);
        spool.Append(first, Speech(0.5));
        Assert.True(await spool.Keep(first));
        var second = spool.Begin(Now.AddMinutes(1));
        spool.Append(second, Speech(1));

        Assert.True(await spool.Keep(second));

        Assert.Equal([DictationTakeSpool.KeptFileName], Files());
        Assert.Equal((Now.AddMinutes(1), 16_000L), DictationTakeSpool.ReadHeader(spool.KeptPath));
    }

    [Fact]
    public async Task A_take_that_cannot_be_moved_into_place_is_kept_where_it_was_recorded()
    {
        // Another program holds kept.pcm (here: a folder in its place). The
        // take is not reported lost while its file stays on disk: it is the
        // kept take where it is, reads back, and goes when discarded.
        var spool = Spool();
        var take = spool.Begin(Now);
        spool.Append(take, Speech(0.5));
        Directory.CreateDirectory(spool.KeptPath);

        Assert.True(await spool.Keep(take));

        var read = await spool.ReadKept();
        Assert.False(read.Unavailable);
        Assert.Equal(TimeSpan.FromSeconds(0.5), read.Audio?.Duration);
        Assert.Contains(Files(), f => f.StartsWith(DictationTakeSpool.TakePrefix, StringComparison.Ordinal));

        await spool.DiscardKept();
        // The take's own file went; the folder in kept.pcm's place is not ours.
        Assert.Empty(Files());
        Assert.True(Directory.Exists(spool.KeptPath));
    }

    [Fact]
    public async Task A_take_whose_own_file_is_gone_is_not_kept_anywhere()
    {
        // Only a held kept.pcm keeps a take where it was recorded: a take
        // file someone deleted is not a recording to offer.
        var spool = Spool();
        var take = spool.Begin(Now);
        spool.Append(take, Speech(0.5));
        await spool.Leave(take);
        File.Delete(Assert.Single(Directory.GetFiles(_directory)));

        Assert.False(await spool.Keep(take));
        Assert.Empty(Files());
        Assert.Equal(new DictationKeptAudio(null, Unavailable: false), await spool.ReadKept());
    }

    [Fact]
    public async Task A_take_kept_where_it_was_recorded_is_replaced_by_the_next_kept_take()
    {
        // One kept at a time, wherever it is: once kept.pcm is free again,
        // the next failure takes its place and the earlier file goes.
        var spool = Spool();
        var first = spool.Begin(Now);
        spool.Append(first, Speech(0.5));
        Directory.CreateDirectory(spool.KeptPath);
        Assert.True(await spool.Keep(first));
        Directory.Delete(spool.KeptPath);

        var second = spool.Begin(Now.AddMinutes(1));
        spool.Append(second, Speech(1));
        Assert.True(await spool.Keep(second));

        Assert.Equal([DictationTakeSpool.KeptFileName], Files());
        Assert.Equal(TimeSpan.FromSeconds(1), (await spool.ReadKept()).Audio?.Duration);
    }

    [Fact]
    public async Task A_kept_take_that_cannot_be_opened_just_now_reads_as_unavailable_not_as_nothing()
    {
        var spool = Spool();
        var take = spool.Begin(Now);
        spool.Append(take, Speech(0.5));
        Assert.True(await spool.Keep(take));

        using (new FileStream(spool.KeptPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(new DictationKeptAudio(null, Unavailable: true), await spool.ReadKept());
        }

        Assert.NotNull((await spool.ReadKept()).Audio);
        // A file that is not a take is gone for good, not "just now".
        File.WriteAllText(spool.KeptPath, "damaged");
        Assert.Equal(new DictationKeptAudio(null, Unavailable: false), await spool.ReadKept());
    }

    [Fact]
    public void At_launch_a_take_that_cannot_be_opened_is_left_for_a_later_launch()
    {
        var crashed = Spool();
        var take = crashed.Begin(Now);
        crashed.Append(take, Speech(1.5));
        crashed.Leave(take);
        var path = Assert.Single(Directory.GetFiles(_directory));

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(Spool().AtLaunch(Now.AddHours(1), TimeSpan.FromDays(7)));
        }

        Assert.True(File.Exists(path));
        Assert.Equal(
            new DictationKeptTake(Now, TimeSpan.FromSeconds(1.5), Interrupted: true),
            Spool().AtLaunch(Now.AddHours(1), TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task A_take_that_holds_nothing_is_not_kept()
    {
        var spool = Spool();
        var take = spool.Begin(Now);

        Assert.False(await spool.Keep(take));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task A_take_whose_write_failed_is_never_offered_and_nothing_throws_into_the_capture()
    {
        // The folder is a file: the take cannot be opened, and every later
        // write is skipped rather than thrown into the capture thread.
        File.WriteAllText(_directory, "not a folder");
        try
        {
            var spool = Spool();
            var take = spool.Begin(Now);
            spool.Append(take, Speech(0.5));

            Assert.True(take.Failed);
            Assert.False(await spool.Keep(take));
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    [Fact]
    public void An_orphaned_spool_at_launch_triggers_the_offer()
    {
        // A Dictation that crashed or quit mid-take left its file.
        var crashed = Spool();
        var take = crashed.Begin(Now);
        crashed.Append(take, Speech(1.5));
        crashed.Leave(take);

        var kept = Spool().AtLaunch(Now.AddHours(1), TimeSpan.FromDays(7));

        Assert.Equal(new DictationKeptTake(Now, TimeSpan.FromSeconds(1.5), Interrupted: true), kept);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files());
    }

    [Fact]
    public async Task At_launch_the_newest_take_is_offered_and_every_other_file_goes()
    {
        var spool = Spool();
        var failed = spool.Begin(Now);
        spool.Append(failed, Speech(0.5));
        Assert.True(await spool.Keep(failed));
        var orphan = spool.Begin(Now.AddMinutes(5));
        spool.Append(orphan, Speech(0.5));
        await spool.Leave(orphan);
        var empty = spool.Begin(Now.AddMinutes(9));
        await spool.Leave(empty);
        File.WriteAllText(Path.Combine(_directory, DictationTakeSpool.TakePrefix + "foreign.pcm"), "not a take");

        var kept = Spool().AtLaunch(Now.AddHours(1), TimeSpan.FromDays(7));

        // The earlier kept take was still offerable: the offer says it was replaced.
        Assert.Equal(new DictationKeptTake(Now.AddMinutes(5), TimeSpan.FromSeconds(0.5), Interrupted: true, ReplacedEarlier: true), kept);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files());
    }

    [Fact]
    public async Task A_kept_take_older_than_a_week_is_deleted_at_launch_unoffered()
    {
        var spool = Spool();
        var take = spool.Begin(Now);
        spool.Append(take, Speech(0.5));
        Assert.True(await spool.Keep(take));

        Assert.NotNull(Spool().AtLaunch(Now.AddDays(7) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(7)));
        Assert.Null(Spool().AtLaunch(Now.AddDays(7), TimeSpan.FromDays(7)));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task A_kept_take_found_at_launch_is_not_an_interrupted_one()
    {
        var spool = Spool();
        var take = spool.Begin(Now);
        spool.Append(take, Speech(0.5));
        Assert.True(await spool.Keep(take));

        Assert.Equal(new DictationKeptTake(Now, TimeSpan.FromSeconds(0.5), Interrupted: false), Spool().AtLaunch(Now, TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task No_folder_no_offer_and_a_gone_kept_take_reads_as_nothing()
    {
        Assert.Null(Spool().AtLaunch(Now, TimeSpan.FromDays(7)));
        // Gone is not "cannot be opened just now": nothing to keep.
        Assert.Equal(new DictationKeptAudio(null, Unavailable: false), await Spool().ReadKept());
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Discarding_the_kept_take_deletes_it()
    {
        var spool = Spool();
        var take = spool.Begin(Now);
        spool.Append(take, Speech(0.5));
        Assert.True(await spool.Keep(take));

        await spool.DiscardKept();

        Assert.Empty(Files());
    }

    [Fact]
    public void The_retry_s_deadline_is_two_seconds_a_second_on_top_of_thirty()
    {
        // LIMIT 2026-10-10: a missed deadline (30 s + the length) retries
        // once with 30 s + 2x the length.
        var options = new DictationWorkerPlan("python.exe", "runtime", "model").Transcriber;
        var audio = TimeSpan.FromMinutes(4);

        Assert.Equal(TimeSpan.FromSeconds(30) + audio, AsrWorkerTranscriber.TranscribeTimeoutFor(options, audio));
        Assert.Equal(TimeSpan.FromSeconds(30) + audio + audio, AsrWorkerTranscriber.RetryTimeoutFor(options, audio));
    }
}
