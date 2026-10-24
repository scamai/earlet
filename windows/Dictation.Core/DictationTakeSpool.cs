using System.Buffers.Binary;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core;

/// <summary>
/// One take as the spool writes it: its file, its start, and the conversion
/// of its buffers to 16 kHz PCM16 as they arrive.
/// </summary>
public sealed class DictationTake
{
    internal DictationTake(string path, DateTimeOffset startedAt)
    {
        Path = path;
        StartedAt = startedAt;
    }

    /// <summary>When the press was admitted, on the wall clock.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>How much audio the take holds so far, from its 16 kHz sample
    /// count: what "your words up to m:ss" and "Discard m:ss of speech?" name.</summary>
    public TimeSpan Captured => Converter.Duration;

    /// <summary>Whether a write failed: the file no longer holds the whole
    /// take, so it is never offered as one.</summary>
    public bool Failed => _failed;

    internal string Path { get; }

    internal DictationTakeConverter Converter { get; } = new();

    /// <summary>The open file. File-work thread only.</summary>
    internal FileStream? Stream { get; set; }

    private volatile bool _failed;

    internal void Fail() => _failed = true;
}

/// <summary>The take kept for Recover last dictation.</summary>
/// <param name="RecordedAt">When it was recorded.</param>
/// <param name="Length">How much audio it holds.</param>
/// <param name="Interrupted">It was found at launch, left by a Dictation
/// that closed or crashed while it was recorded or transcribed; false for a
/// take kept because it could not be transcribed.</param>
/// <param name="ReplacedEarlier">Keeping it deleted an earlier take that was
/// still offered: one is kept at a time, and the section says so.</param>
public sealed record DictationKeptTake(
    DateTimeOffset RecordedAt, TimeSpan Length, bool Interrupted, bool ReplacedEarlier = false);

/// <summary>What reading the kept take back found.</summary>
/// <param name="Audio">Its audio; null when there is none to give.</param>
/// <param name="Unavailable">It could not be opened or read just now (another
/// program holds it, say): it is not damaged, so it stays for another try.
/// False with no audio means it is gone, is not a take this build wrote, or
/// holds nothing.</param>
public sealed record DictationKeptAudio(MicrophoneCapture? Audio, bool Unavailable);

/// <summary>
/// The take spool: every take's audio on disk while it is recorded, deleted
/// once its words went in, and the last one that could not be transcribed
/// kept for Recover last dictation (LIMIT 2026-10-10, rule zero: no stop loses
/// words).
/// </summary>
/// <remarks>
/// <para>
/// <b>The files.</b> In <see cref="DictationTakeOptions.Directory"/>
/// (<c>%LOCALAPPDATA%\Dictation\recovery</c>): one <c>take-&lt;id&gt;.pcm</c>
/// per take while it is recorded and transcribed, and at most one
/// <see cref="KeptFileName"/>. Each is a 24-byte header (<c>DICTTAKE</c>,
/// format 1, the sample rate, the start as UTC ticks) and then the take as
/// little-endian 16-bit mono at 16 kHz, 32 KB a second, appended as it
/// arrives and flushed with every buffer, so a crash keeps everything said
/// before it. Nothing else is written: no words, no app, no window.
/// </para>
/// <para>
/// <b>Off the capture thread, in order.</b> The capture thread only converts
/// (<see cref="DictationTakeConverter"/>); opening, every write, closing,
/// keeping, deleting and reading run one at a time on
/// <see cref="DictationTakeOptions.RunFileWork"/>, so a slow disk never
/// stalls the microphone and a take's file is closed before it is moved. A
/// failure is logged by its type and marks the take
/// (<see cref="DictationTake.Failed"/>); dictation goes on without it.
/// </para>
/// <para>
/// <b>Kept for a week.</b> A kept take is offered until it is recovered or
/// discarded, or for <see cref="DictationTakeOptions.KeptFor"/> at most: the
/// controller deletes it when the week runs out while Dictation runs, and the
/// next launch deletes an older one (<see cref="AtLaunch"/>).
/// </para>
/// <para>
/// <b>Never deleted for a failure to touch it.</b> A file that cannot be
/// opened just now (another program holds it) is not a damaged one: it is
/// neither read as nothing nor deleted. A take whose move to
/// <see cref="KeptFileName"/> fails stays the kept take where it is.
/// </para>
/// </remarks>
public sealed class DictationTakeSpool
{
    /// <summary>The one kept take's file name.</summary>
    public const string KeptFileName = "kept.pcm";

    /// <summary>A take's file name before its id.</summary>
    public const string TakePrefix = "take-";

    /// <summary>Every spool file's extension.</summary>
    public const string Extension = ".pcm";

    /// <summary>The header's length in bytes.</summary>
    public const int HeaderBytes = 24;

    /// <summary>The header's format number.</summary>
    public const int Format = 1;

    private static ReadOnlySpan<byte> Magic => "DICTTAKE"u8;

    private readonly string _directory;
    private readonly Func<Action, Task> _run;
    private readonly ILogger _log;

    /// <summary>Where the kept take is: <see cref="KeptPath"/>, or a take's
    /// own file when moving it there failed. File-work thread only, and
    /// <see cref="AtLaunch"/> before any take.</summary>
    private string? _keptFile;

    public DictationTakeSpool(string directory, Func<Action, Task> run, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Where the files are.</summary>
    public string Directory => _directory;

    /// <summary>The kept take's path.</summary>
    public string KeptPath => Path.Combine(_directory, KeptFileName);

    // ─── One take ───────────────────────────────────────────────────────

    /// <summary>A take starts: its file is created and its header written.</summary>
    public DictationTake Begin(DateTimeOffset startedAt)
    {
        var take = new DictationTake(
            Path.Combine(_directory, TakePrefix + Guid.NewGuid().ToString("N") + Extension), startedAt);
        Queue("open a take", take, () =>
        {
            System.IO.Directory.CreateDirectory(_directory);
            var stream = new FileStream(take.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            take.Stream = stream;
            stream.Write(Header(startedAt));
            stream.Flush();
        });
        return take;
    }

    /// <summary>A buffer of <paramref name="take"/> arrived. On the device's
    /// capture thread: converts there, writes on the file-work thread.</summary>
    public void Append(DictationTake take, AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(take);
        short[] pcm;
        try
        {
            pcm = take.Converter.Convert(chunk);
        }
        catch (Exception error)
        {
            // Never into the capture thread.
            Failed("convert a buffer", take, error);
            return;
        }

        if (pcm.Length == 0 || take.Failed)
        {
            return;
        }

        var bytes = new byte[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm[i]);
        }

        Queue("write a buffer", take, () =>
        {
            if (take.Stream is { } stream && !take.Failed)
            {
                stream.Write(bytes);
                stream.Flush();
            }
        });
    }

    /// <summary>The take's words went in, or it was cancelled: its file goes.</summary>
    public Task Discard(DictationTake take) => Queue("delete a take", take, () =>
    {
        CloseFile(take);
        File.Delete(take.Path);
    });

    /// <summary>The take ends and stays where it is: Dictation is closing
    /// under it, and the next launch offers it (<see cref="AtLaunch"/>).</summary>
    public Task Leave(DictationTake take) => Queue("close a take", take, () => CloseFile(take));

    /// <summary>
    /// The take could not be transcribed: it becomes the kept take, replacing
    /// any older one.
    /// </summary>
    /// <remarks>A move that fails (another program holds <see cref="KeptFileName"/>)
    /// leaves the take the kept one where it is, still recoverable, rather
    /// than report it lost while its file stays on disk.</remarks>
    /// <returns>Whether it is kept: false when the take had already failed
    /// half-written or held no audio, and its file is deleted.</returns>
    public Task<bool> Keep(DictationTake take)
    {
        ArgumentNullException.ThrowIfNull(take);
        var kept = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Queue("keep a take", take, () =>
        {
            try
            {
                CloseFile(take);
                if (take.Failed || take.Converter.Samples == 0)
                {
                    File.Delete(take.Path);
                    kept.TrySetResult(false);
                    return;
                }

                var earlier = _keptFile;
                try
                {
                    File.Move(take.Path, KeptPath, overwrite: true);
                    _keptFile = KeptPath;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    && error is not (FileNotFoundException or DirectoryNotFoundException))
                {
                    // kept.pcm is held; a take whose own file is gone is not
                    // kept anywhere, and fails as any other file work does.
                    _log.LogWarning(
                        "The take could not be moved into place ({Error}); it is kept where it was recorded",
                        error.GetType().Name);
                    _keptFile = take.Path;
                }

                if (earlier is not null && earlier != _keptFile)
                {
                    // One kept at a time: the earlier one, wherever it was.
                    TryDelete(earlier);
                }

                kept.TrySetResult(true);
            }
            finally
            {
                kept.TrySetResult(false);
            }
        });
        return kept.Task;
    }

    // ─── The kept take ──────────────────────────────────────────────────

    /// <summary>
    /// At launch, on the caller's thread: what is left from before. A take
    /// a crash or a quit left behind becomes the kept take if it is the
    /// newest; every other file is deleted, and so is a kept take older than
    /// <paramref name="keptFor"/>, an empty one and one that cannot be read.
    /// </summary>
    /// <remarks>A file that cannot be opened just now is left alone, neither
    /// offered nor deleted, for a later launch; one file that cannot be
    /// deleted costs nothing but itself.</remarks>
    /// <returns>The kept take, or null.</returns>
    public DictationKeptTake? AtLaunch(DateTimeOffset now, TimeSpan keptFor)
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return null;
            }

            var found = System.IO.Directory.EnumerateFiles(_directory, "*" + Extension)
                .Where(path => Path.GetFileName(path) is { } name
                    && (name == KeptFileName || name.StartsWith(TakePrefix, StringComparison.Ordinal)))
                .Select(path => new Found(path, ReadHeader(path, out var unavailable), unavailable))
                .Where(f => !f.Unavailable)
                .ToList();
            bool Offerable(Found f) => f.Header is { Samples: > 0 } header && now - header.StartedAt < keptFor;
            var chosen = found
                .Where(Offerable)
                .OrderByDescending(f => f.Header!.Value.StartedAt)
                .FirstOrDefault();
            // Another take that was still offerable goes with the rest: the
            // section says the one offered replaced it.
            var replaced = found.Any(f => !ReferenceEquals(f, chosen) && Offerable(f));
            foreach (var file in found.Where(f => !ReferenceEquals(f, chosen)))
            {
                TryDelete(file.Path);
            }

            if (chosen?.Header is not { } header)
            {
                return null;
            }

            var interrupted = Path.GetFileName(chosen.Path) != KeptFileName;
            _keptFile = chosen.Path;
            if (interrupted)
            {
                try
                {
                    File.Move(chosen.Path, KeptPath, overwrite: true);
                    _keptFile = KeptPath;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Offered where it is.
                    _log.LogWarning("A take left at launch could not be moved into place ({Error})", error.GetType().Name);
                }
            }

            if (replaced)
            {
                _log.LogInformation("At launch, an earlier saved recording was deleted: one is kept at a time, the newest");
            }

            return new DictationKeptTake(
                header.StartedAt,
                TimeSpan.FromSeconds((double)header.Samples / WorkflowAudio.SampleRateHz),
                interrupted,
                replaced);
        }
        catch (Exception error)
        {
            _log.LogWarning("The recovery folder could not be read at launch ({Error})", error.GetType().Name);
            return null;
        }
    }

    /// <summary>The kept take's audio, read on the file-work thread. A file
    /// that could not be opened or read just now says so
    /// (<see cref="DictationKeptAudio.Unavailable"/>) rather than read as
    /// nothing, so the caller keeps it.</summary>
    public Task<DictationKeptAudio> ReadKept()
    {
        var read = new TaskCompletionSource<DictationKeptAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Queue("read the kept take", null, () =>
        {
            try
            {
                var audio = ReadAudio(_keptFile ?? KeptPath, out var unavailable);
                read.TrySetResult(new DictationKeptAudio(audio, unavailable));
            }
            finally
            {
                // Whatever threw is not a verdict on the file: it stays.
                read.TrySetResult(new DictationKeptAudio(null, Unavailable: true));
            }
        });
        return read.Task;
    }

    /// <summary>The kept take was recovered or discarded: it goes.</summary>
    public Task DiscardKept() => Queue("delete the kept take", null, () =>
    {
        File.Delete(_keptFile ?? KeptPath);
        _keptFile = null;
    });

    // ─── The format ─────────────────────────────────────────────────────

    /// <summary>The header for a take started at <paramref name="startedAt"/>.</summary>
    public static byte[] Header(DateTimeOffset startedAt)
    {
        var header = new byte[HeaderBytes];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), Format);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), WorkflowAudio.SampleRateHz);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), startedAt.UtcTicks);
        return header;
    }

    /// <summary>A spool file's start and sample count; null when it is not
    /// one this build wrote, or cannot be opened.</summary>
    public static (DateTimeOffset StartedAt, long Samples)? ReadHeader(string path) => ReadHeader(path, out _);

    /// <summary><see cref="ReadHeader(string)"/>, telling a file that could
    /// not be opened (<paramref name="unavailable"/>) from one that is not a
    /// take or is gone.</summary>
    private static (DateTimeOffset StartedAt, long Samples)? ReadHeader(string path, out bool unavailable)
    {
        unavailable = false;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[HeaderBytes];
            if (file.ReadAtLeast(header, HeaderBytes, throwOnEndOfStream: false) < HeaderBytes
                || !header[..8].SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4)) != Format
                || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)) != WorkflowAudio.SampleRateHz)
            {
                return null;
            }

            var ticks = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(16, 8));
            if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return null;
            }

            return (new DateTimeOffset(ticks, TimeSpan.Zero), (file.Length - HeaderBytes) / 2);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            unavailable = true;
            return null;
        }
    }

    /// <summary>A spool file's audio, as the transcriber takes it; null when
    /// it is not one this build wrote, holds nothing, or cannot be read.</summary>
    public static MicrophoneCapture? ReadAudio(string path) => ReadAudio(path, out _);

    /// <summary><see cref="ReadAudio(string)"/>, telling a file that could
    /// not be opened or read (<paramref name="unavailable"/>) from one that
    /// holds nothing to give.</summary>
    private static MicrophoneCapture? ReadAudio(string path, out bool unavailable)
    {
        if (ReadHeader(path, out unavailable) is not { Samples: > 0 } header)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            unavailable = true;
            return null;
        }

        var count = (int)Math.Min(header.Samples, (bytes.Length - HeaderBytes) / 2);
        var pcm = new short[count];
        for (var i = 0; i < count; i++)
        {
            pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(HeaderBytes + (i * 2), 2));
        }

        return new MicrophoneCapture(
            pcm, WorkflowAudio.SampleRateHz, WorkflowAudio.Channels,
            TimeSpan.FromSeconds((double)count / WorkflowAudio.SampleRateHz));
    }

    /// <summary>A spool file found at launch, with its header if it has one,
    /// and whether it could not be opened.</summary>
    private sealed record Found(string Path, (DateTimeOffset StartedAt, long Samples)? Header, bool Unavailable);

    // ─── The file-work queue ────────────────────────────────────────────

    /// <summary>Delete one file; a failure is logged by its type and costs
    /// only that file.</summary>
    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("A file in the recovery folder could not be deleted ({Error})", error.GetType().Name);
        }
    }

    private static void CloseFile(DictationTake take)
    {
        var stream = take.Stream;
        take.Stream = null;
        stream?.Dispose();
    }

    /// <summary>One item of file work: in order, and never throwing. A
    /// failure is logged by its type and, for a take, marks it failed.</summary>
    private Task Queue(string what, DictationTake? take, Action work)
    {
        try
        {
            return _run(() =>
            {
                try
                {
                    work();
                }
                catch (Exception error)
                {
                    Failed(what, take, error);
                }
            });
        }
        catch (Exception error)
        {
            Failed(what, take, error);
            return Task.CompletedTask;
        }
    }

    /// <summary>A failure, once per take. The take's file is closed by the
    /// take's own end (<see cref="Discard"/>, <see cref="Keep"/>,
    /// <see cref="Leave"/>), on the file-work thread, never here: this can
    /// run on the capture thread.</summary>
    private void Failed(string what, DictationTake? take, Exception error)
    {
        var first = take is not null && !take.Failed;
        // A take that failed half-written is never offered as whole.
        take?.Fail();
        if (take is null || first)
        {
            // The type only: an I/O message carries the path, and the path
            // the user's name.
            _log.LogWarning("The take spool could not {What} ({Error})", what, error.GetType().Name);
        }
    }
}
