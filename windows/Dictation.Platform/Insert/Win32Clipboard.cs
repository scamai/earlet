using System.Runtime.InteropServices;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using static Dictation.Platform.Insert.Win32InsertInterop;

namespace Dictation.Platform.Insert;

/// <summary>
/// workflows P-07 — <see cref="IClipboard"/> against the real clipboard.
/// </summary>
/// <remarks>
/// <para>
/// One attempt, no retry. <c>OpenClipboard</c> fails when another process is
/// mid-operation on it, and the honest answer to that is <c>false</c>: the
/// caller's response is to not type, which is the safe outcome and is already
/// the demo's fallback. A retry loop here would be a second timing mechanism
/// alongside P-06's, in a phase whose scope explicitly excludes retries.
/// </para>
/// <para>
/// Ownership of the allocation is the one subtle part. On success the system
/// takes the handle and this must not free it; on any failure before that, this
/// still owns it and must. Both paths are below and neither is shared.
/// </para>
/// <para>
/// <b>Every write is marked as transient.</b> What goes through here is a
/// dictated sentence on its way into a text box, or the same sentence left
/// behind after a refused paste, and neither is something the user chose to
/// copy. Unmarked, Windows keeps each one in Win+V clipboard history, uploads
/// it to the user's other devices when clipboard sync is on, and every
/// clipboard manager on the machine records it — which is every utterance,
/// written somewhere the product promised it would not be. The three
/// registered formats in <see cref="Plan"/> are the documented way to say no
/// to all three, and they go into the same <c>OpenClipboard</c> session as the
/// text, because the history service reads the clipboard once it is closed
/// and a marker written in a second session would arrive after that read. The
/// macOS writer tags its item <c>org.nspasteboard.ConcealedType</c> for the
/// same reason.
/// </para>
/// </remarks>
public sealed class Win32Clipboard : IClipboard
{
    /// <summary>
    /// Registered format that tells every clipboard monitor — Win+V history,
    /// cloud sync, third-party managers — to leave the content alone. Any data
    /// will do; see <see cref="Plan"/> for why it is four zero bytes rather
    /// than nothing.
    /// </summary>
    internal const string ExcludeFromMonitors = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>Registered format: a DWORD of zero keeps the content out of
    /// Win+V clipboard history. A one would ask for the opposite.</summary>
    internal const string CanIncludeInHistory = "CanIncludeInClipboardHistory";

    /// <summary>Registered format: a DWORD of zero keeps the content off the
    /// cloud clipboard that syncs to the user's other devices.</summary>
    internal const string CanUploadToCloud = "CanUploadToCloudClipboard";

    private readonly ILogger<Win32Clipboard> _logger;

    public Win32Clipboard(ILogger<Win32Clipboard> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Every format one write puts on the clipboard, in the order it puts
    /// them there: the text, then the three markers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure function so the list can be checked without a clipboard: the
    /// whole privacy guarantee is "these three names, with these payloads, on
    /// every write", and a marker dropped from here is a compile-clean change
    /// that puts every dictated sentence back into Win+V history.
    /// </para>
    /// <para>
    /// <b>All three payloads are a DWORD of zero.</b> For the two
    /// <c>Can…</c> formats zero is the meaning — the value one explicitly
    /// allows. For <see cref="ExcludeFromMonitors"/> any data counts, and the
    /// obvious choice of none is the one that does not work: <c>GlobalLock</c>
    /// returns null for a zero-byte block, and <c>SetClipboardData</c> has been
    /// measured refusing a zero-length handle (AutoHotkey's clipboard code
    /// records it on Windows 8 and pads every empty format to one byte for
    /// that reason).
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<ClipboardWrite> Plan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // The terminator is part of what CF_UNICODETEXT expects, so it is
        // part of what is allocated and copied.
        var chars = (text + '\0').ToCharArray();
        var bytes = new byte[chars.Length * sizeof(char)];
        Buffer.BlockCopy(chars, 0, bytes, 0, bytes.Length);

        return
        [
            ClipboardWrite.Standard(CF_UNICODETEXT, bytes),
            ClipboardWrite.Registered(ExcludeFromMonitors, new byte[sizeof(uint)]),
            ClipboardWrite.Registered(CanIncludeInHistory, new byte[sizeof(uint)]),
            ClipboardWrite.Registered(CanUploadToCloud, new byte[sizeof(uint)]),
        ];
    }

    public bool TrySet(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var plan = Plan(text);

        if (!OpenClipboard(IntPtr.Zero))
        {
            _logger.LogWarning(
                "Could not open the clipboard (error {Error}); not typing.",
                Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            return Write(plan, new OpenClipboardSession(_logger), _logger);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Carries out <paramref name="plan"/> inside a clipboard session that is
    /// already open: the first entry is the content, every later one a marker.
    /// True when the content is on the clipboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="TrySet"/> so the sequence can be held to
    /// account without a clipboard. <see cref="Plan"/> alone says what SHOULD
    /// be written; this is what is, and a marker loop deleted from here would
    /// otherwise leave every test of the plan green while every dictated
    /// sentence went back into Win+V history.
    /// </para>
    /// <para>
    /// Every block this allocates is either taken by the clipboard or freed
    /// here, exactly once — the handoff the class remarks call the subtle part,
    /// and the reason it is spelled out on each path rather than shared.
    /// </para>
    /// </remarks>
    internal static bool Write(IReadOnlyList<ClipboardWrite> plan, IClipboardSession session, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);

        // The text is allocated and filled BEFORE the clipboard is emptied, so
        // a failed allocation leaves the user's clipboard exactly as it was.
        // False does not always mean that: a text refused after the emptying
        // leaves the clipboard empty, and this cannot put back what it never
        // read. The dictation insert, which holds a snapshot, does — when its
        // restore finds the clipboard still empty.
        var content = plan[0];
        var block = session.Allocate(content.Payload);
        if (block == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!session.Empty())
            {
                logger.LogWarning(
                    "Could not empty the clipboard (error {Error}).",
                    session.LastError);
                return false;
            }

            if (!session.Set(content.Format, block))
            {
                logger.LogWarning(
                    "Clipboard refused the text (error {Error}).",
                    session.LastError);
                return false;
            }

            // Handed over. Clearing the local reference is what stops the
            // finally below from freeing memory the system now owns.
            block = IntPtr.Zero;

            // Best effort, and deliberately not a reason to return false: the
            // text is already on the clipboard, and false tells the caller it
            // is not, so a text ready to paste would not be pasted. A marker
            // that fails leaves this write as visible to clipboard history as
            // every write was before the markers existed, and says so — and
            // the markers after it are still written, because each one closes
            // a different door.
            for (var i = 1; i < plan.Count; i++)
            {
                SetMarker(plan[i], session, logger);
            }

            return true;
        }
        finally
        {
            if (block != IntPtr.Zero)
            {
                session.Free(block);
            }
        }
    }

    /// <summary>
    /// Writes one marker into the session that is already open. Owns its own
    /// allocation until the clipboard takes it, the same handoff as the
    /// text's.
    /// </summary>
    private static void SetMarker(ClipboardWrite marker, IClipboardSession session, ILogger logger)
    {
        var format = session.Register(marker.Name!);
        if (format == 0)
        {
            logger.LogWarning(
                "Could not register the {Format} clipboard format (error {Error}); this text may reach clipboard history.",
                marker.Name,
                session.LastError);
            return;
        }

        var block = session.Allocate(marker.Payload);
        if (block == IntPtr.Zero)
        {
            return;
        }

        if (!session.Set(format, block))
        {
            logger.LogWarning(
                "Clipboard refused the {Format} marker (error {Error}); this text may reach clipboard history.",
                marker.Name,
                session.LastError);
            session.Free(block);
        }
    }

    /// <summary>
    /// The clipboard <see cref="TrySet"/> has open. Each member is one Win32
    /// call, or for <see cref="Allocate"/> the four that fill a block, so the
    /// error <see cref="LastError"/> reads right after a failed member is that
    /// member's own.
    /// </summary>
    private sealed class OpenClipboardSession(ILogger logger) : IClipboardSession
    {
        public int LastError => Marshal.GetLastWin32Error();

        public bool Empty() => EmptyClipboard();

        public uint Register(string name) => RegisterClipboardFormat(name);

        public bool Set(uint format, IntPtr block) => SetClipboardData(format, block) != IntPtr.Zero;

        public void Free(IntPtr block) => GlobalFree(block);

        /// <summary>
        /// A movable block holding <paramref name="payload"/>, or zero. The
        /// caller owns a non-zero result until the clipboard takes it.
        /// </summary>
        public IntPtr Allocate(byte[] payload)
        {
            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)payload.Length);
            if (handle == IntPtr.Zero)
            {
                logger.LogWarning("Could not allocate {Bytes} bytes for the clipboard.", payload.Length);
                return IntPtr.Zero;
            }

            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                logger.LogWarning("Could not lock the clipboard allocation.");
                GlobalFree(handle);
                return IntPtr.Zero;
            }

            try
            {
                Marshal.Copy(payload, 0, target, payload.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            return handle;
        }
    }
}

/// <summary>
/// The calls <see cref="Win32Clipboard.Write"/> makes against a clipboard that
/// is already open. A seam only so the write sequence — what is emptied, in
/// what order formats go on, who frees what — can be tested without a
/// clipboard; the one real implementation is private to
/// <see cref="Win32Clipboard"/>.
/// </summary>
internal interface IClipboardSession
{
    /// <summary>The OS error left by the member that just failed.</summary>
    int LastError { get; }

    /// <summary>A block holding <paramref name="payload"/>, or zero. The
    /// caller owns it until <see cref="Set"/> succeeds.</summary>
    IntPtr Allocate(byte[] payload);

    /// <summary>Drops everything the clipboard holds and takes ownership of
    /// it for this session.</summary>
    bool Empty();

    /// <summary>The session's number for a named format, or zero.</summary>
    uint Register(string name);

    /// <summary>Puts <paramref name="block"/> on the clipboard as
    /// <paramref name="format"/>. True means the clipboard now owns the block;
    /// false means the caller still does.</summary>
    bool Set(uint format, IntPtr block);

    /// <summary>Frees a block the clipboard did not take.</summary>
    void Free(IntPtr block);
}

/// <summary>
/// One format a <see cref="Win32Clipboard"/> write puts on the clipboard:
/// either a standard format by number, or a registered one by name, which is
/// turned into this session's number at write time.
/// </summary>
internal readonly record struct ClipboardWrite(uint Format, string? Name, byte[] Payload)
{
    public static ClipboardWrite Standard(uint format, byte[] payload) => new(format, null, payload);

    public static ClipboardWrite Registered(string name, byte[] payload) => new(0, name, payload);
}
