using System.Runtime.InteropServices;
using Dictation.Services;
using Microsoft.Extensions.Logging;

namespace Dictation.Infrastructure;

/// <summary>
/// What a snapshot does with one format the clipboard lists.
/// </summary>
public enum ClipboardFormatTreatment
{
    /// <summary>Memory: copied out byte for byte and written back the same.</summary>
    Copy,

    /// <summary>
    /// Windows makes this one again from a format that was kept — ANSI and OEM
    /// text from Unicode text, the plain bitmap from the one with an alpha
    /// channel. Copying it would double the memory a snapshot holds and put
    /// back nothing a paste could not already get.
    /// </summary>
    RebuiltByWindows,

    /// <summary>
    /// Not memory at all: a GDI bitmap, palette or metafile handle, an
    /// owner-drawn display format, or a number in the private or GDI-object
    /// ranges. Its bytes are a handle into somebody else's process or GDI, and
    /// writing them back would hand a pasting application a dangling pointer.
    /// </summary>
    NotMemory,

    /// <summary>
    /// An OLE link or embedding format whose mere reading makes the owning
    /// program do something: Word adds a bookmark to the user's document when
    /// <c>Link Source</c> or <c>ObjectLink</c> is rendered, and Outlook's Word
    /// editor raised "an outgoing call cannot be made" dialogs when
    /// <c>Native</c> or <c>Embed Source</c> was. Never read. Both lists are the
    /// ones AutoHotkey's <c>ClipboardAll</c> arrived at over years of reports.
    /// </summary>
    UnsafeToRead,
}

/// <summary>A format as the clipboard lists it, before anything is read.</summary>
public readonly record struct ListedClipboardFormat(uint Id, string Name);

/// <summary>
/// The two reads a snapshot makes of one format. A seam only so the decisions
/// around them — what to read, what to skip, what is lost — can be tested
/// without a clipboard; the one real implementation is private to
/// <see cref="ClipboardFormatSnapshot"/>.
/// </summary>
public interface IClipboardFormatReader
{
    /// <summary>
    /// How many bytes the format holds, asking its owner to render it first if
    /// it was promised rather than written. Null when nothing came back.
    /// </summary>
    long? SizeOf(uint format);

    /// <summary>The format's bytes, or null when they could not be read.</summary>
    byte[]? Copy(uint format, long size);
}

/// <summary>
/// The calls a restore makes against a clipboard this process already holds
/// open. A seam only so the restore's order — compare, then empty, then write
/// — can be tested without a clipboard; the one real implementation is
/// private to <see cref="ClipboardFormatSnapshot"/>.
/// </summary>
public interface IClipboardRestoreSession
{
    /// <summary>The clipboard's change count, read while it is open.</summary>
    uint SequenceNumber { get; }

    /// <summary>How many formats the clipboard holds, read while it is open.</summary>
    int FormatCount { get; }

    /// <summary>The OS error left by the member that just failed.</summary>
    int LastError { get; }

    /// <summary>Drops everything the clipboard holds.</summary>
    bool Empty();

    /// <summary>The session's number for a named format, or zero.</summary>
    uint Register(string name);

    /// <summary>Writes one format. False when the clipboard refused it.</summary>
    bool Put(uint format, byte[] data);
}

/// <summary>
/// One format a restore writes: a format the snapshot copied, by its
/// <see cref="Id"/>, or a marker the restore adds, with no id, registered by
/// <see cref="Name"/> in the restore's own session.
/// </summary>
public sealed record ClipboardRestoreWrite(uint? Id, string Name, byte[] Data);

/// <summary>
/// dictation insert — the whole Windows clipboard, copied out before a paste
/// borrows it and written back afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every format.</b> A Windows clipboard is one item offered in many
/// formats at once: copied rich text is Unicode text, <c>HTML Format</c> and
/// <c>Rich Text Format</c> together; a screenshot is a device-independent
/// bitmap, often beside other image formats; Explorer's copy is a file list.
/// The text-only read put back the first of these and nothing else, so a
/// screenshot or a file list was simply gone after a dictation and the
/// dictated sentence was left in its place.
/// </para>
/// <para>
/// <b>What is deliberately left out</b> is everything
/// <see cref="Classify"/> does not answer <see cref="ClipboardFormatTreatment.Copy"/>
/// for. In practice a paste after the restore still finds the content —
/// Windows rebuilds the GDI bitmap from the copied DIB and ANSI text from
/// Unicode — with two honest gaps: a vector metafile comes back only as the
/// bitmap or text beside it, and an Office object pastes as its HTML, RTF or
/// picture rather than as an embeddable object, because reading the
/// embedding formats has side effects in the program that owns them.
/// </para>
/// <para>
/// <b>The size budget.</b> Copying a format renders it, and a snapshot holds a
/// second copy of everything for half a second. <see cref="MaxBytes"/> bounds
/// that: 256 MiB keeps a full-resolution screenshot across three 4K monitors
/// (about 100 MB as one bitmap — the second bitmap format is rebuilt, not
/// copied) and stops a gigabyte of copied log from being duplicated into this
/// process. A format over the budget is <see cref="ClipboardContents.Lost"/>,
/// and the insert says the clipboard did not come back whole.
/// </para>
/// <para>
/// <b>What cannot be seen from Linux</b> is any of the Win32 below
/// <see cref="Build"/>: enumeration, rendering by a delayed-render owner, and
/// the single calls that write back. Those are an open device check.
/// Everything that decides — what is read, in what order, what counts as
/// lost — is in the two pure functions, and the restore's own order runs
/// against <see cref="IClipboardRestoreSession"/>; all of it is tested.
/// </para>
/// </remarks>
public static class ClipboardFormatSnapshot
{
    /// <summary>The most a snapshot copies, in bytes. See the remarks.</summary>
    public const long MaxBytes = 256L * 1024 * 1024;

    // Standard format numbers, from winuser.h. Fixed since Windows 3.1.
    internal const uint CF_TEXT = 1;
    internal const uint CF_BITMAP = 2;
    internal const uint CF_METAFILEPICT = 3;
    internal const uint CF_OEMTEXT = 7;
    internal const uint CF_DIB = 8;
    internal const uint CF_PALETTE = 9;
    internal const uint CF_UNICODETEXT = 13;
    internal const uint CF_ENHMETAFILE = 14;
    internal const uint CF_DIBV5 = 17;
    internal const uint CF_OWNERDISPLAY = 0x0080;
    internal const uint CF_DSPBITMAP = 0x0082;
    internal const uint CF_DSPMETAFILEPICT = 0x0083;
    internal const uint CF_DSPENHMETAFILE = 0x008E;
    internal const uint CF_PRIVATEFIRST = 0x0200;
    internal const uint CF_GDIOBJLAST = 0x03FF;

    /// <summary>
    /// Formats whose data is legitimately null: Visual Studio, Scintilla and
    /// others mark a whole-line or column copy by the format's PRESENCE, and
    /// put no data behind it. Kept as an empty format so a paste in the same
    /// editor still pastes a line rather than a fragment.
    /// </summary>
    private static readonly string[] PresenceOnly = ["MSDEVColumnSelect", "MSDEVLineSelect"];

    /// <summary>
    /// The markers that keep a write out of Win+V history, cloud sync and
    /// every other clipboard monitor — the names <c>Win32Clipboard.Plan</c>
    /// gives the transcript write, repeated here because this project does
    /// not reference the workflows adapter. A test holds the two lists equal.
    /// </summary>
    private static readonly string[] HistoryMarkers =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
    ];

    // ─── Decisions (pure) ───────────────────────────────────────────

    /// <summary>
    /// What to do with <paramref name="format"/>, given the formats already
    /// <paramref name="kept"/>. Pure, so every rule can be pinned on Linux.
    /// </summary>
    public static ClipboardFormatTreatment Classify(
        uint format, string name, IReadOnlyCollection<uint> kept)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(kept);

        if (format is CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or CF_ENHMETAFILE
            or CF_OWNERDISPLAY or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE
            // The private (0x200–0x2FF) and GDI-object (0x300–0x3FF) ranges.
            or >= CF_PRIVATEFIRST and <= CF_GDIOBJLAST)
        {
            return ClipboardFormatTreatment.NotMemory;
        }

        if (name.StartsWith("Link Source", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ObjectLink", StringComparison.OrdinalIgnoreCase)
            || name.Equals("OwnerLink", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Native", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Embed Source", StringComparison.OrdinalIgnoreCase))
        {
            return ClipboardFormatTreatment.UnsafeToRead;
        }

        return RebuiltFrom(format, kept)
            ? ClipboardFormatTreatment.RebuiltByWindows
            : ClipboardFormatTreatment.Copy;
    }

    /// <summary>
    /// Whether Windows synthesises <paramref name="format"/> from one already
    /// <paramref name="kept"/>, losing nothing a paste would want.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The richer of each pair is the one kept, and the poorer is rebuilt
    /// from it. ANSI text is rebuilt from Unicode, never the reverse: Unicode
    /// made from ANSI has already lost every character outside the code page,
    /// so Unicode text is always copied. The bitmaps follow the same rule:
    /// <c>CF_DIBV5</c> can carry an alpha channel and <c>CF_DIB</c> cannot,
    /// so when both are listed the V5 is kept and the plain DIB rebuilt from
    /// it. Keeping the DIB instead dropped the transparency of a picture whose
    /// owner listed a plain DIB first and its alpha V5 second, and nothing
    /// reported it.
    /// </para>
    /// <para>
    /// The reverse bitmap answer — a V5 rebuilt from a kept DIB — is used for
    /// one case only: a V5 over the size budget whose DIB fitted. The picture
    /// comes back, without any transparency it had, and that is not reported
    /// as lost, because telling the user their picture is gone would be the
    /// larger untruth. <see cref="Build"/> never reaches it otherwise, because
    /// the DIB is decided after the V5.
    /// </para>
    /// </remarks>
    private static bool RebuiltFrom(uint format, IReadOnlyCollection<uint> kept) => format switch
    {
        CF_TEXT or CF_OEMTEXT => kept.Contains(CF_UNICODETEXT),
        CF_DIB => kept.Contains(CF_DIBV5),
        CF_DIBV5 => kept.Contains(CF_DIB),
        _ => false,
    };

    /// <summary>
    /// The poorer half of each pair, decided in the second pass of
    /// <see cref="Build"/> once it is known whether the richer half was kept.
    /// </summary>
    private static bool DecidedLast(uint format) => format is CF_TEXT or CF_OEMTEXT or CF_DIB;

    /// <summary>
    /// Copies what <paramref name="listed"/> names through
    /// <paramref name="reader"/>, and accounts for everything it did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two passes.</b> ANSI and OEM text are decided last, once it is known
    /// whether Unicode text was kept, because a clipboard written by an ANSI
    /// program lists its CF_TEXT BEFORE the Unicode Windows synthesised from
    /// it. The plain DIB is decided last for the same reason: an owner may
    /// list it before the alpha V5, and it is read only if the V5 was not
    /// kept.
    /// </para>
    /// <para>
    /// <b>Kept formats keep their listed order</b>, which is the owner's order
    /// of preference: a pasting application takes the first format it
    /// understands, so reordering would change what it pastes.
    /// </para>
    /// <para>
    /// <b>What counts as lost</b> is a format over the size budget that nothing
    /// kept will rebuild — and, when nothing at all could be kept from a
    /// clipboard that listed something, every listed format: restoring that as
    /// an empty clipboard and calling it success would be the old bug in a new
    /// place. A format whose owner returned no data is omitted, not lost: no
    /// paste could have had it either.
    /// </para>
    /// </remarks>
    public static ClipboardContents Build(
        IReadOnlyList<ListedClipboardFormat> listed, IClipboardFormatReader reader, long maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(reader);

        var kept = new ClipboardFormatData?[listed.Count];
        var keptIds = new HashSet<uint>();
        var lost = new List<ListedClipboardFormat>();
        var omitted = new List<string>();
        long total = 0;

        foreach (var lastPass in new[] { false, true })
        {
            for (var i = 0; i < listed.Count; i++)
            {
                var format = listed[i];
                if (DecidedLast(format.Id) != lastPass)
                {
                    continue;
                }

                // Classified BEFORE anything is read: for the unsafe formats
                // the read is the harm, and for handles it is meaningless.
                var treatment = Classify(format.Id, format.Name, keptIds);
                if (treatment != ClipboardFormatTreatment.Copy)
                {
                    omitted.Add(format.Name);
                    continue;
                }

                var size = reader.SizeOf(format.Id);
                if (size is null)
                {
                    if (PresenceOnly.Contains(format.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        kept[i] = new ClipboardFormatData(format.Id, format.Name, []);
                        keptIds.Add(format.Id);
                    }
                    else
                    {
                        omitted.Add($"{format.Name} (no data)");
                    }

                    continue;
                }

                if (size.Value > maxBytes - total)
                {
                    lost.Add(format);
                    continue;
                }

                var data = reader.Copy(format.Id, size.Value);
                if (data is null)
                {
                    omitted.Add($"{format.Name} (unreadable)");
                    continue;
                }

                total += data.Length;
                kept[i] = new ClipboardFormatData(format.Id, format.Name, data);
                keptIds.Add(format.Id);
            }
        }

        var formats = kept.OfType<ClipboardFormatData>().ToArray();

        // A bitmap over the budget whose twin fitted is not lost: Windows
        // rebuilds it from the twin.
        var reallyLost = lost
            .Where(format => !RebuiltFrom(format.Id, keptIds))
            .Select(format => $"{format.Name} (too large to keep)")
            .ToList();
        omitted.AddRange(lost
            .Where(format => RebuiltFrom(format.Id, keptIds))
            .Select(format => format.Name));

        if (formats.Length == 0 && listed.Count > 0 && reallyLost.Count == 0)
        {
            reallyLost.AddRange(listed.Select(format => format.Name));
        }

        return new ClipboardContents(formats, reallyLost, omitted);
    }

    /// <summary>
    /// The <c>CF_</c> name of a standard format, or null for a number that is
    /// not one. Registered formats are named by <c>GetClipboardFormatName</c>.
    /// </summary>
    public static string? StandardName(uint format) => format switch
    {
        CF_TEXT => "CF_TEXT",
        CF_BITMAP => "CF_BITMAP",
        CF_METAFILEPICT => "CF_METAFILEPICT",
        4 => "CF_SYLK",
        5 => "CF_DIF",
        6 => "CF_TIFF",
        CF_OEMTEXT => "CF_OEMTEXT",
        CF_DIB => "CF_DIB",
        CF_PALETTE => "CF_PALETTE",
        10 => "CF_PENDATA",
        11 => "CF_RIFF",
        12 => "CF_WAVE",
        CF_UNICODETEXT => "CF_UNICODETEXT",
        CF_ENHMETAFILE => "CF_ENHMETAFILE",
        15 => "CF_HDROP",
        16 => "CF_LOCALE",
        CF_DIBV5 => "CF_DIBV5",
        CF_OWNERDISPLAY => "CF_OWNERDISPLAY",
        0x0081 => "CF_DSPTEXT",
        CF_DSPBITMAP => "CF_DSPBITMAP",
        CF_DSPMETAFILEPICT => "CF_DSPMETAFILEPICT",
        CF_DSPENHMETAFILE => "CF_DSPENHMETAFILE",
        _ => null,
    };

    /// <summary>
    /// Everything a restore writes, in order: every format the snapshot kept,
    /// in its listed order, then each history marker the snapshot does not
    /// already carry. Nothing at all for an empty snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the restore is marked.</b> Clipboard history takes a write to
    /// the clipboard as a new item, and the restore is a write. Unmarked, a
    /// dictation could put what the user had copied into history a second
    /// time — the first entry is there from their own copy — and offer it to
    /// cloud sync again; whether Windows folds identical items together has
    /// not been seen on a device. The markers are the transcript write's,
    /// with its payload: a DWORD of zero, which is the meaning for the two
    /// <c>Can…</c> formats and four bytes rather than none for the exclusion
    /// format, because a zero-length block cannot be locked.
    /// </para>
    /// <para>
    /// <b>The user's own markers win.</b> A marker the snapshot already holds
    /// goes back as the copied content had it, in its listed place, and is not
    /// written a second time, which would replace it. So an item that opted
    /// into history explicitly still does, and may appear there twice; that
    /// is its owner's choice, not this restore's to undo.
    /// </para>
    /// <para>
    /// An empty snapshot gets no markers: they would leave the clipboard
    /// holding something where the user had nothing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ClipboardRestoreWrite> RestorePlan(ClipboardContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        if (contents.Formats.Count == 0)
        {
            return [];
        }

        var copied = contents.Formats.Select(format => new ClipboardRestoreWrite(format.Id, format.Name, format.Data));
        var added = HistoryMarkers
            .Where(marker => !contents.Formats.Any(format => format.Name.Equals(marker, StringComparison.OrdinalIgnoreCase)))
            .Select(marker => new ClipboardRestoreWrite(null, marker, new byte[sizeof(uint)]));
        return copied.Concat(added).ToArray();
    }

    // ─── The clipboard (Win32) ──────────────────────────────────────

    /// <summary>
    /// Copies every format the clipboard holds that <see cref="Classify"/>
    /// answers Copy for, within <see cref="MaxBytes"/> (the class remarks say
    /// what is left out). Null when it could not be opened or listed — another process holding it is the usual reason —
    /// which the insert reports as a clipboard it could not give back.
    /// </summary>
    /// <remarks>
    /// One attempt, no retry, the rule every clipboard call in this product
    /// keeps. The clipboard is held open for the whole read, so the list and
    /// the bytes describe one clipboard rather than two.
    /// </remarks>
    public static ClipboardContents? Capture(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!OpenClipboard(IntPtr.Zero))
        {
            logger.LogWarning(
                "Could not open the clipboard to copy it before inserting (error {Error}).",
                Marshal.GetLastWin32Error());
            return null;
        }

        try
        {
            // Zero ends the list both when it is finished and when it failed,
            // and only the error code tells them apart — so the code is
            // cleared before every call. A stale one left by an earlier call
            // on this thread would make every EMPTY clipboard read as
            // unreadable, and every dictation done with nothing copied would
            // then report a clipboard it could not give back.
            var ids = new List<uint>();
            uint format = 0;
            while (true)
            {
                Marshal.SetLastSystemError(0);
                format = EnumClipboardFormats(format);
                if (format == 0)
                {
                    break;
                }

                ids.Add(format);
            }

            var error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                logger.LogWarning("Could not list the clipboard's formats (error {Error}).", error);
                return null;
            }

            var listed = ids.Select(id => new ListedClipboardFormat(id, NameOf(id))).ToList();
            var contents = Build(listed, new OpenClipboardReader());

            // Names and sizes only. The bytes are the user's — a password, as
            // often as not — and never reach a log.
            logger.LogDebug(
                "Copied {Kept} of {Listed} clipboard formats ({Bytes} bytes); left out [{Omitted}]; lost [{Lost}].",
                contents.Formats.Count,
                listed.Count,
                contents.Formats.Sum(format => (long)format.Data.Length),
                string.Join(", ", contents.Omitted),
                string.Join(", ", contents.Lost));
            return contents;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Opens the clipboard and restores <paramref name="contents"/> into it —
    /// see the overload that takes a session for the order and the condition.
    /// <see cref="SnapshotRestoreResult.Failed"/> also when the clipboard
    /// could not be opened.
    /// </summary>
    public static SnapshotRestoreResult Restore(
        ClipboardContents contents, ClipboardRestoreCondition? condition, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(logger);

        if (!OpenClipboard(IntPtr.Zero))
        {
            logger.LogWarning(
                "Could not open the clipboard to put it back (error {Error}).",
                Marshal.GetLastWin32Error());
            return SnapshotRestoreResult.Failed;
        }

        try
        {
            return Restore(contents, condition, new OpenClipboardWriter(), logger);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// The restore inside a clipboard <paramref name="session"/> that is
    /// already open: check <paramref name="condition"/>, empty the clipboard,
    /// then write <see cref="RestorePlan"/> — every format back in order, and
    /// the history markers after them. An empty snapshot leaves the clipboard
    /// empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The condition is checked here, with the clipboard open, and before
    /// it is emptied.</b> While this process holds the clipboard no other
    /// process can write it, so a count that matches now still matches when
    /// the snapshot goes on. Compared before the open, a copy that landed
    /// between the comparison and the emptying was overwritten by the older
    /// snapshot, and the comparison had said it was safe. A condition that
    /// asks for an empty clipboard is checked the same way, after the count.
    /// </para>
    /// <para>
    /// A refused format fails the restore, and the formats after it are still
    /// written, because the text back without its picture beats neither. A
    /// marker that cannot go on does not fail it: the user's content is back
    /// either way, and the restore is then only as visible to clipboard
    /// history as every restore was before the markers.
    /// </para>
    /// </remarks>
    public static SnapshotRestoreResult Restore(
        ClipboardContents contents,
        ClipboardRestoreCondition? condition,
        IClipboardRestoreSession session,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);

        if (condition is { } expected && session.SequenceNumber != expected.Sequence)
        {
            logger.LogDebug("The clipboard changed before it could be put back; leaving the newer contents in place.");
            return SnapshotRestoreResult.Changed;
        }

        if (condition is { HoldsNothing: true } && session.FormatCount != 0)
        {
            logger.LogDebug("The clipboard still holds something; there is nothing to put back.");
            return SnapshotRestoreResult.Changed;
        }

        if (!session.Empty())
        {
            logger.LogWarning(
                "Could not empty the clipboard to put it back (error {Error}).",
                session.LastError);
            return SnapshotRestoreResult.Failed;
        }

        var refused = new List<string>();
        foreach (var write in RestorePlan(contents))
        {
            if (write.Id is { } id)
            {
                if (!session.Put(id, write.Data))
                {
                    refused.Add(write.Name);
                }

                continue;
            }

            var number = session.Register(write.Name);
            if (number == 0 || !session.Put(number, write.Data))
            {
                logger.LogWarning(
                    "Could not mark the restored clipboard with {Format}; it may reach clipboard history again.",
                    write.Name);
            }
        }

        if (refused.Count > 0)
        {
            logger.LogWarning(
                "The clipboard refused {Count} format(s) on the way back: {Formats}.",
                refused.Count,
                string.Join(", ", refused));
            return SnapshotRestoreResult.Failed;
        }

        return SnapshotRestoreResult.Restored;
    }

    /// <summary>The clipboard's change count. Zero when this session has no
    /// clipboard access, which compares equal to itself and so leaves the
    /// restore unconditional.</summary>
    public static uint SequenceNumber() => GetClipboardSequenceNumber();

    /// <summary>
    /// Writes one format into the open clipboard. Owns its allocation until
    /// <c>SetClipboardData</c> takes it — the same handoff as
    /// <c>Win32Clipboard</c>'s, and the reason it is spelled out on both paths.
    /// </summary>
    private static bool Put(uint format, byte[] data)
    {
        // Never zero bytes: GlobalLock refuses a zero-size block, and
        // SetClipboardData has been measured refusing one. A presence-only
        // format goes back as a single zeroed byte, which is what AutoHotkey's
        // restore does for the same reason.
        var size = Math.Max(data.Length, 1);
        var handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)(uint)size);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (data.Length > 0)
            {
                var target = GlobalLock(handle);
                if (target == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    Marshal.Copy(data, 0, target, data.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }

            if (SetClipboardData(format, handle) == IntPtr.Zero)
            {
                return false;
            }

            // Handed over: the clipboard owns it now and frees it.
            handle = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                GlobalFree(handle);
            }
        }
    }

    private static string NameOf(uint format)
    {
        if (StandardName(format) is { } standard)
        {
            return standard;
        }

        if (format >= 0xC000)
        {
            var buffer = new char[256];
            var length = GetClipboardFormatName(format, buffer, buffer.Length);
            if (length > 0)
            {
                return new string(buffer, 0, length);
            }
        }

        return $"#0x{format:X4}";
    }

    /// <summary>
    /// The reads against the clipboard <see cref="Capture"/> has open. Nothing
    /// here frees anything: the system keeps the handle
    /// <c>GetClipboardData</c> returns.
    /// </summary>
    private sealed class OpenClipboardReader : IClipboardFormatReader
    {
        public long? SizeOf(uint format)
        {
            var handle = GetClipboardData(format);
            return handle == IntPtr.Zero ? null : (long)(ulong)GlobalSize(handle);
        }

        public byte[]? Copy(uint format, long size)
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            if (size == 0)
            {
                // GlobalLock fails on a zero-size block; there is nothing to
                // copy and the format's presence is what is kept.
                return [];
            }

            var source = GlobalLock(handle);
            if (source == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bytes = new byte[size];
                Marshal.Copy(source, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
    }

    /// <summary>
    /// The writes against the clipboard <see cref="Restore(ClipboardContents, ClipboardRestoreCondition?, ILogger)"/>
    /// has open. <see cref="Empty"/> is one Win32 call, so the error
    /// <see cref="LastError"/> reads right after it failed is its own.
    /// </summary>
    private sealed class OpenClipboardWriter : IClipboardRestoreSession
    {
        public uint SequenceNumber => GetClipboardSequenceNumber();

        // Zero also when the call fails, which would read as empty. It needs
        // only the clipboard access OpenClipboard has already proved, so it is
        // not expected to fail here; that is an inference, not a device result.
        public int FormatCount => CountClipboardFormats();

        public int LastError => Marshal.GetLastWin32Error();

        public bool Empty() => EmptyClipboard();

        public uint Register(string name) => RegisterClipboardFormat(name);

        public bool Put(uint format, byte[] data) => ClipboardFormatSnapshot.Put(format, data);
    }

    // Declared here, for this project, rather than borrowed from the workflows
    // interop, which is internal and holds only what a text write needs — the
    // same choice Win32ReadableClipboard made for its text read.
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int CountClipboardFormats();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetClipboardFormatNameW")]
    private static extern int GetClipboardFormatName(uint format, [Out] char[] lpszFormatName, int cchMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
