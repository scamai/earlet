using System.Runtime.InteropServices;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Infrastructure;

/// <summary>
/// dictation M1 — the clipboard, with the read that lets it be given back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> <c>Win32Clipboard</c> in the workflows
/// adapter implements <see cref="IClipboard"/> and nothing else: voice
/// workflows only ever <em>write</em>, so a read was never needed and was
/// correctly never built. Dictation borrows the clipboard from a user who is
/// mid-task and has to hand it back, which needs a read.
/// </para>
/// <para>
/// Without this, dictation still works and still tells the truth — but every
/// single insert returns
/// <see cref="FocusedInsertionOutcome.ClipboardRestoreFailed"/>, because
/// <c>FocusedTextInsertion</c> treats an unreadable clipboard as a restore it
/// cannot perform rather than as one it can skip. That is by design there: an
/// implementation whose clipboard cannot be read can never return Success, and
/// this class is the pressure that design was applying.
/// </para>
/// <para>
/// <b>Composition, not a second implementation.</b> The write delegates to the
/// existing <see cref="IClipboard"/> rather than re-deriving it. Its
/// <c>GlobalAlloc</c> ownership handoff — the system takes the handle on
/// success and must not be freed, this still owns it on every earlier failure
/// and must — is the subtle part of that file, and a copy would be a second
/// place for it to be got wrong.
/// </para>
/// <para>
/// <b>One attempt, no retry</b>, matching the write side. <c>OpenClipboard</c>
/// fails while another process holds it, and the honest answer is "could not
/// read": the caller's response is to report a restore failure rather than to
/// silently leave dictated text where the user's clipboard was.
/// </para>
/// <para>
/// <b>The whole clipboard, not just its text.</b> It also answers
/// <see cref="IClipboardSnapshot"/>, which <c>FocusedTextInsertion</c> prefers
/// when both are offered: the formats a paste ordinarily uses (Unicode text,
/// DIB pictures, file lists, HTML, RTF) are copied out before the paste and
/// written back after it — not metafiles, OLE embedding or private handle
/// formats, nor anything over the size budget — so a copied screenshot, file
/// list or formatted text survives a dictation, and the restore is skipped when the clipboard's change
/// count, read with the clipboard already open for the restore, shows somebody
/// else wrote it during the settle. The work is in
/// <see cref="ClipboardFormatSnapshot"/>; <see cref="TryRead"/> stays as the
/// text-only answer for any caller that asks for only that.
/// </para>
/// <para>
/// The restore writes its own formats rather than going through the
/// composed <see cref="IClipboard"/>, because that seam takes one string. It
/// writes back what was copied, so the user's own markers — a password
/// manager's "keep this out of history" — go back with their content as they
/// were. Where the content carried none, it adds the transcript write's
/// markers, because the restore is itself a write and clipboard history would
/// otherwise take what the user copied a second time; see
/// <see cref="ClipboardFormatSnapshot.RestorePlan"/>.
/// </para>
/// </remarks>
public sealed class Win32ReadableClipboard : IClipboard, IClipboardTextSnapshot, IClipboardSnapshot
{
    private readonly IClipboard _writer;
    private readonly ILogger<Win32ReadableClipboard> _logger;

    public Win32ReadableClipboard(IClipboard writer, ILogger<Win32ReadableClipboard> logger)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool TrySet(string text) => _writer.TrySet(text);

    /// <inheritdoc />
    public ClipboardContents? Capture() => ClipboardFormatSnapshot.Capture(_logger);

    /// <inheritdoc />
    public SnapshotRestoreResult Restore(ClipboardContents contents, ClipboardRestoreCondition? condition) =>
        ClipboardFormatSnapshot.Restore(contents, condition, _logger);

    /// <inheritdoc />
    public uint SequenceNumber => ClipboardFormatSnapshot.SequenceNumber();

    /// <inheritdoc />
    public bool TryRead(out string? text)
    {
        text = null;

        if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            // A successful read of "there is no text here" — an empty
            // clipboard, or one holding an image or a file list. True, not
            // false: the caller distinguishes "nothing to put back" from
            // "could not find out", and only the latter is a failure.
            return true;
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            _logger.LogWarning(
                "Could not open the clipboard to read it (error {Error}).",
                Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                // The format was advertised a moment ago and is gone now, or
                // the owner rendered it and failed. Either way this cannot be
                // put back, so it is a failed read rather than an empty one.
                _logger.LogWarning(
                    "Clipboard advertised text but returned nothing (error {Error}).",
                    Marshal.GetLastWin32Error());
                return false;
            }

            var locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                _logger.LogWarning("Could not lock the clipboard's text for reading.");
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(locked);
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            // Symmetric with the write side: the clipboard is a global lock
            // and must be released on every path, including the ones that
            // return false above.
            CloseClipboard();
        }
    }

    // Declared here rather than reused from the workflows interop, which has
    // only what a write needs. GetClipboardData and IsClipboardFormatAvailable
    // are read-side calls; GlobalLock/GlobalUnlock/Open/Close are redeclared
    // rather than made public over there so this project does not widen the
    // workflows adapter's surface for its own convenience.
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
