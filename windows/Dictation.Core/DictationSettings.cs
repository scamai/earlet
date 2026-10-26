using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dictation.Services;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>What the Windows app remembers between runs.</summary>
/// <param name="HoldVirtualKey">
/// The chosen hold key as a Windows virtual-key code, or 0 for the default.
/// A value this build does not offer resolves to the default, with a warning
/// in the log (<c>DictationHotkeys.ResolveHold</c>).
/// </param>
/// <param name="Enabled">
/// Whether dictation was on when the user last left it. Set by a successful
/// enable, cleared only by Pause; closing the window or quitting leaves it
/// alone, so the next launch comes back the way the user left it.
/// </param>
/// <param name="Language">
/// The recognition language as a Whisper code, or null for automatic
/// detection. A code this build does not offer reads as automatic.
/// </param>
/// <param name="TrayNoticeShown">
/// Whether the one-time "still running in the notification area" notice has
/// been shown, so a window closed to the tray explains itself once and never
/// again.
/// </param>
/// <param name="LearnFromCorrections">
/// "Learn from my corrections": whether a delivered dictation is watched for
/// the user's fixes (LEARNING.md §1; the watching itself is §7). On unless
/// the user turned it off — including in a file written before the switch
/// existed. Off stops the watching only; what was learned stays in use until
/// it is removed.
/// </param>
/// <param name="CompletedFirstDictation">
/// Whether a dictation has ever delivered its words, so the "Try dictation"
/// practice field starts collapsed instead of open. Set once, by the first
/// delivered dictation and nothing else, and never cleared. The one setup
/// value kept: no practice text, history or count.
/// </param>
/// <param name="SilenceStop">
/// "Silence stops hands-free": how much silence ends a hands-free dictation, or
/// never (<see cref="DictationHoldPolicy"/>). 30 s unless the user chose
/// otherwise, including in a file written before the choice existed.
/// </param>
/// <param name="MouseButton">
/// The side button that also starts a dictation, or none. Saved as its id
/// (<see cref="DictationMouseButtons.Id"/>); an id this build does not know
/// reads as none, so a hand-edited or downgraded file never installs a mouse
/// hook the user did not ask for.
/// </param>
public sealed record DictationSettings(
    uint HoldVirtualKey = 0,
    bool Enabled = false,
    string? Language = null,
    bool TrayNoticeShown = false,
    bool LearnFromCorrections = true,
    bool CompletedFirstDictation = false,
    DictationSilenceStop SilenceStop = DictationSilenceStop.After30Seconds,
    DictationMouseButton MouseButton = DictationMouseButton.None);

/// <summary>
/// <see cref="DictationSettings"/> in <c>%LOCALAPPDATA%\Dictation\settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Dictation's own file; the source app's settings are never read. A missing
/// file is the defaults, and so is an empty one (nothing but whitespace): it
/// holds no choices to protect, so the next save writes it. A save that fails
/// keeps the choice for this run and says so in the log; for a choice made in
/// the window (the hold key, the mouse button, the language, "Learn from my
/// corrections") the status line also says it will reset when Dictation
/// restarts.
/// </para>
/// <para>
/// <b>A file that could not be read is never overwritten.</b> Locked by
/// another process or refused by its permissions at launch, it is the defaults
/// for this run, and every save is refused until a later load reads it. A save
/// writes all the keys this type owns from memory, so the first one after such
/// a launch used to replace the user's hold key, language and "learn from my
/// corrections" with the defaults, turning learning back on without a word.
/// <c>learning.json</c>'s store keeps the same rule.
/// </para>
/// <para>
/// <b>A file that reads but does not parse is never overwritten either.</b>
/// Text that is not a JSON object — truncated by a crash mid-write, edited by
/// hand into a mistake — is the defaults for this run, kept exactly as it is
/// on disk, and every save is refused until a later load parses it: the same
/// rule as a file that could not be read. Replacing it with the defaults would
/// destroy the user's choices to save a file the user can still repair. The
/// cost is that no choice made in such a run outlives it. (<c>learning.json</c>
/// differs: its store moves a bad file aside to <c>learning.json.bad</c> and
/// starts empty, LEARNING.md §5.) A save that finds the file went bad after a
/// load that read it is refused too, but only that save: this run did read
/// the user's choices, so once the file parses again the next save merges
/// into it as usual.
/// </para>
/// <para>
/// <b>Written whole.</b> A save writes a temporary file beside the target,
/// flushes it to disk and moves it over the target, retrying the move briefly
/// while another process holds the file open, so a crash or a reader never
/// leaves half a file, or an empty one, in its place.
/// </para>
/// <para>
/// A save rewrites only the keys this type owns and keeps every other key in
/// the file, so a setting added by a newer build (or by another part of the
/// app) survives an older writer.
/// </para>
/// </remarks>
/// <param name="path">The settings file.</param>
/// <param name="log">Where a read or a save that failed is reported.</param>
/// <param name="retryWait">How a replace that failed waits before the next
/// attempt: <see cref="Thread.Sleep(TimeSpan)"/>, unless a test frees the target
/// in between instead of racing a timer.</param>
public sealed class DictationSettingsStore(
    string path, ILogger<DictationSettingsStore> log, Action<TimeSpan>? retryWait = null)
{
    private const string HoldVirtualKeyName = "hold_virtual_key";
    private const string EnabledName = "enabled";
    private const string LanguageName = "language";
    private const string TrayNoticeShownName = "tray_notice_shown";
    private const string LearnFromCorrectionsName = "learn_from_corrections";
    private const string CompletedFirstDictationName = "completed_first_dictation";

    /// <summary>The hands-free silence stop in seconds: 30, 60, or 0 for
    /// never. Any other value reads as 30, the default.</summary>
    private const string SilenceStopName = "hands_free_silence_stop_seconds";

    /// <summary>The mouse button's id: "none", "button4" or "button5".</summary>
    private const string MouseButtonName = "mouse_button";

    private const int ReplaceAttempts = 5;

    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(20);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Action<TimeSpan> _retryWait = retryWait ?? Thread.Sleep;

    /// <summary>The last load found a file it could not read or parse; until a
    /// load reads it, saving would replace choices this run never saw —
    /// including a file the user repaired by hand after that load. Only a load
    /// sets or clears it; a save that meets a file gone bad refuses itself
    /// through <see cref="ReadForUpdate"/>'s null instead.</summary>
    private bool _unread;

    /// <summary>The settings file.</summary>
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public DictationSettings Load()
    {
        string text;
        try
        {
            if (!File.Exists(Path))
            {
                _unread = false;
                return new DictationSettings();
            }

            text = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _unread = false;
            return new DictationSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _unread = true;
            log.LogWarning(ex, "Settings at {Path} could not be read; using the defaults for this run "
                + "and leaving the file as it is", Path);
            return new DictationSettings();
        }

        _unread = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            // No choices to protect: the same as a missing file.
            log.LogInformation("Settings at {Path} are empty; using the defaults", Path);
            return new DictationSettings();
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonObject file)
            {
                _unread = true;
                log.LogWarning("Settings at {Path} are not a JSON object; using the defaults for this run "
                    + "and leaving the file as it is", Path);
                return new DictationSettings();
            }

            var language = Read<string>(file, LanguageName);
            if (language is not null && !SpeechLanguages.IsOffered(language))
            {
                log.LogWarning("Settings name a language this build does not offer; using automatic detection");
                language = null;
            }

            return new DictationSettings(
                Read<uint>(file, HoldVirtualKeyName),
                Read<bool>(file, EnabledName),
                SpeechLanguages.Find(language).Code,
                Read<bool>(file, TrayNoticeShownName),
                Read(file, LearnFromCorrectionsName, fallback: true),
                Read<bool>(file, CompletedFirstDictationName),
                SilenceStopFrom(Read(file, SilenceStopName, fallback: 30)),
                DictationMouseButtons.Normalize(Read<string>(file, MouseButtonName)));
        }
        catch (JsonException ex)
        {
            _unread = true;
            log.LogWarning(ex, "Settings at {Path} could not be parsed; using the defaults for this run "
                + "and leaving the file as it is", Path);
            return new DictationSettings();
        }
    }

    /// <summary>Write the settings, replacing the file in one step.</summary>
    /// <returns>False when the file could not be written, or when the last
    /// load could not read the file this would replace.</returns>
    public bool Save(DictationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_unread)
        {
            log.LogWarning("Settings were not saved: {Path} could not be read or parsed at the last load, "
                + "so it is not replaced", Path);
            return false;
        }

        var temporary = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (ReadForUpdate() is not { } file)
            {
                log.LogWarning("Settings were not saved: {Path} does not parse, so it is not replaced", Path);
                return false;
            }

            file[HoldVirtualKeyName] = settings.HoldVirtualKey;
            file[EnabledName] = settings.Enabled;
            file[LanguageName] = settings.Language;
            file[TrayNoticeShownName] = settings.TrayNoticeShown;
            file[LearnFromCorrectionsName] = settings.LearnFromCorrections;
            file[CompletedFirstDictationName] = settings.CompletedFirstDictation;
            file[SilenceStopName] = SilenceStopSeconds(settings.SilenceStop);
            file[MouseButtonName] = DictationMouseButtons.Id(settings.MouseButton);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(file.ToJsonString(Json)));
                stream.Flush(flushToDisk: true);
            }

            Replace(temporary);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Settings could not be saved to {Path}", Path);
            return false;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Move the flushed temporary file over the target, retried for at most
    /// about 80 ms: on Windows the move fails while another process holds the
    /// target open without delete sharing (an editor, a backup tool, a virus
    /// scanner), and reports that as access denied rather than a sharing
    /// violation. <c>LearningStore</c> waits for its reader the same way.
    /// </summary>
    private void Replace(string temporary)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, Path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReplaceAttempts)
            {
                _retryWait(ReplaceRetryDelay);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temporary file costs a few hundred bytes; the save's
            // result already says whether the settings were replaced.
        }
    }

    /// <summary>The file as it is now, or an empty object when it is missing
    /// or empty. A file that does not parse is <b>not</b> an empty object: it
    /// is content this type cannot account for, so null is returned and the
    /// save in flight is refused. Later saves read the file again, so one the
    /// user repairs is merged into as usual. A file that cannot be read
    /// throws, and the save fails rather than replace it.</summary>
    private JsonObject? ReadForUpdate()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new JsonObject();
            }

            var text = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();
            }

            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            // Unparseable: the file is kept, never replaced.
            return null;
        }
    }

    private static DictationSilenceStop SilenceStopFrom(int seconds) => seconds switch
    {
        60 => DictationSilenceStop.After60Seconds,
        0 => DictationSilenceStop.Off,
        _ => DictationSilenceStop.After30Seconds,
    };

    private static int SilenceStopSeconds(DictationSilenceStop stop) => stop switch
    {
        DictationSilenceStop.After60Seconds => 60,
        DictationSilenceStop.Off => 0,
        _ => 30,
    };

    /// <summary>One key, or the type's default when it is absent or of
    /// another type.</summary>
    private static T? Read<T>(JsonObject file, string name) => Read<T?>(file, name, fallback: default);

    /// <summary>One key, or <paramref name="fallback"/> when it is absent or
    /// of another type.</summary>
    private static T Read<T>(JsonObject file, string name, T fallback)
    {
        try
        {
            return file[name] is JsonValue value && value.TryGetValue<T>(out var read) ? read : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }
}
