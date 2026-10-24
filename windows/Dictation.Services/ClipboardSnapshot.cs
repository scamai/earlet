using Dictation.Speech;

namespace Dictation.Services;

// The whole-clipboard half of the insert's read seam: what a snapshot holds
// and how one is put back. FocusedTextInsertion consumes it; the Win32
// implementation is Win32ReadableClipboard. In its own file only to keep
// FocusedTextInserter.cs under the repository's file-size cap.

/// <summary>
/// One clipboard format, copied out as bytes.
/// </summary>
/// <param name="Id">
/// The format's number: 13 for Unicode text, 15 for a file list, 17 for a
/// bitmap with alpha, and a session-assigned number from 0xC000 up for a
/// registered format such as <c>HTML Format</c>. A registered number is stable
/// for the whole Windows session, which comfortably outlives the half second
/// a snapshot is held.
/// </param>
/// <param name="Name">
/// What the format is called — the registered name, or the <c>CF_</c> name of
/// a standard one. For log lines and tests; the restore writes by
/// <paramref name="Id"/>.
/// </param>
/// <param name="Data">
/// The bytes, exactly as the clipboard held them. Never logged: this is the
/// user's own content, a password included.
/// </param>
public sealed record ClipboardFormatData(uint Id, string Name, byte[] Data);

/// <summary>
/// Everything the clipboard held that could be copied, and an honest account
/// of what could not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds of "not kept", and only one of them is a loss.</b>
/// <see cref="Omitted"/> is what was left out on purpose: a format Windows
/// rebuilds from one that was kept (plain ANSI text from Unicode text, the
/// plain bitmap from the one with alpha), a handle rather than memory, or an
/// OLE link format whose mere reading has side effects in the program that
/// owns it. Pasting
/// after the restore still finds the content. <see cref="Lost"/> is what the
/// user had and will not get back — a payload over the size budget, or a
/// clipboard of which nothing at all could be kept — and a non-empty list
/// makes the insert report
/// <see cref="FocusedInsertionOutcome.ClipboardRestoreFailed"/>.
/// </para>
/// <para>
/// No formats and nothing lost is an empty clipboard, and restoring it means
/// emptying the clipboard — not leaving the dictated text there because there
/// was "nothing to put back".
/// </para>
/// </remarks>
public sealed class ClipboardContents
{
    public ClipboardContents(
        IEnumerable<ClipboardFormatData> formats,
        IEnumerable<string>? lost = null,
        IEnumerable<string>? omitted = null)
    {
        ArgumentNullException.ThrowIfNull(formats);
        Formats = formats.ToArray();
        Lost = lost?.ToArray() ?? [];
        Omitted = omitted?.ToArray() ?? [];
    }

    /// <summary>A clipboard that held nothing.</summary>
    public static ClipboardContents Empty { get; } = new([]);

    /// <summary>Every format kept, in the order the clipboard listed them —
    /// which is the owner's order of preference, and a pasting application
    /// takes the first one it understands.</summary>
    public IReadOnlyList<ClipboardFormatData> Formats { get; }

    /// <summary>Names of formats the user had that will not come back.</summary>
    public IReadOnlyList<string> Lost { get; }

    /// <summary>Names of formats left out on purpose; for the log only.</summary>
    public IReadOnlyList<string> Omitted { get; }
}

/// <summary>
/// Reading the whole clipboard — the formats a paste ordinarily uses, not just
/// its text — so the thing that overwrites it can put it back. The Win32
/// implementation leaves some formats out (metafiles, OLE embedding and
/// private handle formats, anything over its size budget) and says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why text was not enough.</b> Screenshot, dictate, paste is ordinary use.
/// <see cref="IClipboardTextSnapshot"/> read the screenshot as "no text", the
/// insert treated that as nothing to restore, and the user's next Ctrl+V
/// pasted the dictated sentence again where the picture should have been. A
/// file list copied in Explorer went the same way, and copied rich text came
/// back as plain text with its formatting gone. The macOS inserter snapshots
/// every pasteboard type, and this is the same promise on Windows.
/// </para>
/// <para>
/// <b>Why it carries a sequence number.</b> The restore runs half a second
/// after the paste, and the clipboard is a shared resource for all of that
/// half second. Windows counts every change to it; the insert reads the count
/// right after writing the transcript and puts the snapshot back only if the
/// count has not moved since. A copy the user made in that window — or a
/// password manager filling a field — survives instead of being overwritten
/// by an older snapshot. The macOS inserter makes the same check with
/// <c>changeCount</c>.
/// </para>
/// <para>
/// <b>Why the restore makes the comparison itself.</b> A count compared from
/// outside and a restore that then opens the clipboard leave a gap between
/// the two in which somebody else can still write. Once this process holds
/// the clipboard open nobody else can, so <see cref="Restore"/> takes the
/// expected count in a <see cref="ClipboardRestoreCondition"/> and compares
/// it there, before anything is emptied.
/// </para>
/// <para>
/// Additive, and discovered on the <see cref="IClipboard"/> exactly as
/// <see cref="IClipboardTextSnapshot"/> is, for the reasons given there: one
/// object, so the clipboard read, written and restored is necessarily the
/// same clipboard.
/// </para>
/// </remarks>
public interface IClipboardSnapshot
{
    /// <summary>
    /// Copies the clipboard's formats that a paste ordinarily uses (the Win32
    /// implementation lists what it leaves out). Null when the clipboard could
    /// not be read at all, which is the case that makes a restore impossible.
    /// </summary>
    ClipboardContents? Capture();

    /// <summary>
    /// Empties the clipboard and writes every format in
    /// <paramref name="contents"/> back, in order — unless, once the clipboard
    /// is open, it is not the clipboard <paramref name="condition"/> describes,
    /// in which case nothing is written. No condition makes the restore
    /// unconditional. An empty snapshot leaves the clipboard empty.
    /// </summary>
    SnapshotRestoreResult Restore(ClipboardContents contents, ClipboardRestoreCondition? condition);

    /// <summary>
    /// The clipboard's change count — <c>GetClipboardSequenceNumber</c>.
    /// Compared, never interpreted: only "the same as a moment ago" means
    /// anything.
    /// </summary>
    uint SequenceNumber { get; }
}

/// <summary>
/// The clipboard a whole-clipboard restore expects to find, checked with the
/// clipboard open and before anything on it is emptied.
/// </summary>
/// <param name="Sequence">The change count it must still have.</param>
/// <param name="HoldsNothing">
/// Whether it must also hold no format at all. Set when undoing a transcript
/// write that failed: the count read after it has moved whether that write
/// emptied the clipboard or another process wrote it in the same moment, and
/// only the emptying is the insert's to undo.
/// </param>
public readonly record struct ClipboardRestoreCondition(uint Sequence, bool HoldsNothing = false);

/// <summary>How a whole-clipboard restore went.</summary>
public enum SnapshotRestoreResult
{
    /// <summary>Every format in the snapshot was written back.</summary>
    Restored,

    /// <summary>
    /// Nothing was written: with the clipboard open it was not the clipboard
    /// the condition described, so somebody else wrote it and what they
    /// wrote is newer than the snapshot.
    /// </summary>
    Changed,

    /// <summary>The clipboard could not be opened or emptied, or refused a
    /// format of the snapshot.</summary>
    Failed,
}
