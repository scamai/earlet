using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Learning;

/// <summary>What <see cref="LearningStore.Load"/> found.</summary>
public enum LearningFileStatus
{
    /// <summary>No file yet. The store starts empty and the first save creates it.</summary>
    Missing,

    /// <summary>The file was read.</summary>
    Loaded,

    /// <summary>The file did not parse or had another schema. It is now
    /// <c>learning.json.bad</c> (replacing an older one) and the store starts
    /// empty.</summary>
    KeptAside,

    /// <summary>The file could not be read, or a bad file could not be moved
    /// aside. The store starts empty and refuses to write, so a file it has
    /// not read is never overwritten; the next load tries again.</summary>
    Unreadable,
}

/// <summary>The state a load produced, and what it found on disk.</summary>
public sealed record LearningLoadResult(LearningState State, LearningFileStatus Status);

/// <summary>
/// <c>learning.json</c> (LEARNING.md §1, §5): the one file the host writes and
/// the worker reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic replace.</b> A save writes a temporary file beside the target,
/// flushes it to disk and moves it over the target, so the worker, which
/// re-reads the file whenever its size or mtime changes, never sees half of
/// one. Saves and loads of the same path are serialized in this process, across
/// store instances.
/// </para>
/// <para>
/// <b>A bad file is kept, not overwritten.</b> A file that does not parse, or
/// has another schema, is moved to <c>learning.json.bad</c> and the store
/// starts empty. A store only writes after a load that read the file, found
/// none, or moved a bad one aside.
/// </para>
/// <para>
/// <b>IO failures are results, never exceptions.</b> The log records what
/// happened and to which file, never the file's contents.
/// </para>
/// </remarks>
public sealed class LearningStore
{
    /// <summary>The subdirectory of the data root, shared with the worker's
    /// <c>bias_aliases.json</c>.</summary>
    public const string DirectoryName = "dictation";

    public const string FileName = "learning.json";

    public const string BadFileName = "learning.json.bad";

    private const int ReplaceAttempts = 5;

    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(20);

    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Readable CJK in the file; the HTML-sensitive characters stay escaped.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ILogger _log;
    private readonly object _gate;
    private bool _writable;

    /// <param name="directory">The directory that holds <c>learning.json</c>:
    /// <c>&lt;data&gt;\dictation</c> in the app, a temporary one in tests.</param>
    public LearningStore(string directory, ILogger<LearningStore>? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = System.IO.Path.GetFullPath(directory);
        FilePath = System.IO.Path.Combine(Directory, FileName);
        BadFilePath = System.IO.Path.Combine(Directory, BadFileName);
        _log = log ?? NullLogger<LearningStore>.Instance;
        _gate = Gates.GetOrAdd(FilePath, _ => new object());
    }

    /// <summary>The app's store: <c>%LOCALAPPDATA%\Dictation\dictation\learning.json</c>.</summary>
    public static LearningStore ForUser(DictationPaths paths, ILogger<LearningStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new LearningStore(System.IO.Path.Combine(paths.Root, DirectoryName), log);
    }

    public string Directory { get; }

    public string FilePath { get; }

    public string BadFilePath { get; }

    // ─── Load ───

    /// <summary>Read the file. Never throws for anything on disk.</summary>
    public LearningLoadResult Load()
    {
        lock (_gate)
        {
            byte[] bytes;
            try
            {
                if (!File.Exists(FilePath))
                {
                    _writable = true;
                    return new LearningLoadResult(new LearningState(), LearningFileStatus.Missing);
                }

                bytes = File.ReadAllBytes(FilePath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                _writable = true;
                return new LearningLoadResult(new LearningState(), LearningFileStatus.Missing);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                _writable = false;
                _log.LogWarning(ex, "Learned vocabulary at {Path} could not be read; it is left as it is", FilePath);
                return new LearningLoadResult(new LearningState(), LearningFileStatus.Unreadable);
            }

            if (Parse(bytes) is { } state)
            {
                _writable = true;
                return new LearningLoadResult(state, LearningFileStatus.Loaded);
            }

            try
            {
                File.Move(FilePath, BadFilePath, overwrite: true);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                _writable = false;
                _log.LogWarning(ex, "Learned vocabulary at {Path} is not readable and could not be moved aside; " +
                    "it is left as it is; learning continues in memory and is saved once the file can be read", FilePath);
                return new LearningLoadResult(new LearningState(), LearningFileStatus.Unreadable);
            }

            _writable = true;
            _log.LogWarning("Learned vocabulary at {Path} ({Bytes} bytes) is not {Schema}; kept aside as {Bad}, " +
                "starting empty", FilePath, bytes.Length, LearningLimits.Schema, BadFileName);
            return new LearningLoadResult(new LearningState(), LearningFileStatus.KeptAside);
        }
    }

    /// <summary>
    /// The state in <paramref name="utf8"/>, or null when it is not strict
    /// UTF-8 JSON of schema <c>dictation.learning/1</c> with every entry whole.
    /// Missing lists are empty; unknown keys are kept.
    /// </summary>
    public static LearningState? Parse(ReadOnlySpan<byte> utf8)
    {
        LearningState? state;
        try
        {
            state = JsonSerializer.Deserialize<LearningState>(StrictUtf8.GetString(utf8), Json);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or NotSupportedException)
        {
            return null;
        }

        return state is not null && Normalize(state) ? state : null;
    }

    // ─── Save ───

    /// <summary>
    /// Replace the file with <paramref name="state"/>.
    /// </summary>
    /// <returns>False when nothing was written: the disk refused, or this store
    /// has not read the file it would replace.</returns>
    public bool Save(LearningState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var bytes = StrictUtf8.GetBytes(Serialize(state));
        lock (_gate)
        {
            if (!_writable)
            {
                _log.LogWarning("Learned vocabulary was not saved: {Path} has not been read, so it is not replaced",
                    FilePath);
                return false;
            }

            var temporary = System.IO.Path.Combine(Directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                Replace(temporary);
                return true;
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                _log.LogWarning(ex, "Learned vocabulary could not be saved to {Path}", FilePath);
                return false;
            }
            finally
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>The file's text for <paramref name="state"/>: indented UTF-8
    /// JSON, keys in LEARNING.md §5 order.</summary>
    public static string Serialize(LearningState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.Serialize(state, Json);
    }

    // ─── Helpers ───

    /// <summary>
    /// Move the flushed temporary file over the target. Retried briefly, for
    /// at most about 80 ms: on Windows the move fails while another process
    /// holds the target open without delete sharing, which is how Python's
    /// <c>open()</c> opens it when the worker reads the file. Windows reports
    /// that as access denied, not as a sharing violation.
    /// </summary>
    private void Replace(string temporary)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, FilePath, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                Thread.Sleep(ReplaceRetryDelay);
            }
        }
    }

    private static bool Normalize(LearningState state)
    {
        if (state.Schema != LearningLimits.Schema)
        {
            return false;
        }

        state.Words ??= [];
        state.Aliases ??= [];
        state.Pending ??= [];
        state.Removed ??= new RemovedEntries();
        state.Removed.Words ??= [];
        state.Removed.Aliases ??= [];
        foreach (var pending in state.Pending)
        {
            if (pending is not null)
            {
                pending.Dictations ??= [];
            }
        }

        return state.Words.All(w => w is { Text: not null, Origin: not null })
            && state.Aliases.All(a => a is { Heard: not null, Meant: not null, Origin: not null })
            && state.Pending.All(p => p is { Heard: not null, Meant: not null } && p.Dictations.All(id => id is not null))
            && state.Removed.Words.All(r => r is { Key: not null })
            && state.Removed.Aliases.All(r => r is { Heard: not null, Meant: not null });
    }

    private static bool IsIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            // A stray temporary file costs a few kilobytes; the save's result
            // already says whether the target was replaced.
        }
    }
}
