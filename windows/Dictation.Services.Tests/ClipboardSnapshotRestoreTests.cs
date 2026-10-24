using System.Text;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// dictation insert — giving back the WHOLE clipboard, not just its text.
///
/// <para>
/// The defect these pin: the restore could see only Unicode text. A copied
/// screenshot or file list read as "no text", the insert treated that as
/// nothing to put back, and the dictated sentence stayed on the clipboard —
/// so the next Ctrl+V pasted the dictation again where the picture should
/// have been. Copied rich text came back as plain text. And the restore wrote
/// over anything copied during the half-second settle, because nothing
/// checked whether the clipboard had changed hands.
/// </para>
///
/// <para>
/// The clipboard here is a fake with several formats and a change count, the
/// shape of the Windows clipboard <c>Win32ReadableClipboard</c> fronts. What it
/// cannot show is that Windows itself takes the formats back; that is a device
/// check (VERIFY.md).
/// </para>
/// </summary>
public sealed class ClipboardSnapshotRestoreTests
{
    private const uint UnicodeText = 13;
    private const uint DibV5 = 17;
    private const uint FileList = 15;

    // ─── Formats other than text come back ──────────────────────────

    [Fact]
    public async Task A_copied_image_is_back_after_the_insert_and_the_transcript_is_gone()
    {
        // Screenshot, dictate, paste. The image is the only format there —
        // which is exactly the clipboard the text-only read called empty.
        var image = Format(DibV5, "CF_DIBV5", 0x28, 0, 0, 0, 0xFF, 0x00, 0x7F);
        var clipboard = new MultiFormatClipboard(image);
        var paste = new RecordingPaste(clipboard);

        var result = await Insert(clipboard, paste, "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        // The paste read the transcript...
        Assert.Equal("the spoken sentence", paste.TextWhenSent);
        // ...and afterwards the clipboard holds the image, byte for byte, and
        // no text at all — so the next Ctrl+V pastes the picture, not the
        // dictation a second time.
        AssertHolds(clipboard, image);
    }

    [Fact]
    public async Task A_copied_file_list_is_back_after_the_insert()
    {
        var files = Format(FileList, "CF_HDROP", 0x14, 0, 0, 0, (byte)'C', 0, (byte)':', 0, 0, 0, 0, 0);
        var clipboard = new MultiFormatClipboard(files);

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        AssertHolds(clipboard, files);
    }

    [Fact]
    public async Task Rich_text_comes_back_with_its_HTML_and_RTF_not_as_plain_text()
    {
        // Copied from a browser or Word: the same words three ways. The old
        // restore put back only the first, so pasting into a document after a
        // dictation lost the bold, the links and the table.
        var text = Text("Quarterly results");
        var html = Format(0xC0A1, "HTML Format", Encoding.UTF8.GetBytes("<b>Quarterly</b> results"));
        var rtf = Format(0xC0A2, "Rich Text Format", Encoding.ASCII.GetBytes(@"{\rtf1 \b Quarterly\b0 results}"));
        var clipboard = new MultiFormatClipboard(text, html, rtf);

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        // All three, in the order they were listed — a pasting application
        // takes the first format it understands, so order is part of what was
        // copied.
        AssertHolds(clipboard, text, html, rtf);
    }

    // ─── Nothing copied stays nothing ───────────────────────────────

    [Fact]
    public async Task An_empty_clipboard_ends_empty_not_holding_the_transcript()
    {
        // The text-only path cannot write "nothing" and so left the dictated
        // sentence sitting on an empty clipboard indefinitely. Emptiness is a
        // state like any other, and it is the one the user had.
        var clipboard = new MultiFormatClipboard();

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal(1, clipboard.Restores);
        Assert.Empty(clipboard.Current);
    }

    // ─── A copy made during the settle wins ─────────────────────────

    [Fact]
    public async Task A_copy_made_while_the_paste_settles_is_not_overwritten()
    {
        // The user pastes, sees the sentence land, and copies something else
        // before half a second is up — or a password manager fills a field.
        // That content is newer than the snapshot, and putting the snapshot
        // back would eat it.
        var original = Text("what was copied before the dictation");
        var copiedDuringSettle = Text("what was copied during the paste");
        var clipboard = new MultiFormatClipboard(original);
        var paste = new RecordingPaste(clipboard)
        {
            AfterSend = () => clipboard.ThirdPartyCopy(copiedDuringSettle),
        };

        var result = await Insert(clipboard, paste, "the spoken sentence");

        // Still Success: the clipboard holds what the user last chose, which
        // is what a restore is for. The reason says why nothing was written.
        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Contains("changed", result.Reason);
        Assert.Equal(0, clipboard.Restores);
        AssertHolds(clipboard, copiedDuringSettle);
    }

    [Fact]
    public async Task A_copy_that_lands_as_the_restore_begins_is_not_overwritten()
    {
        // The settle is over and the count has not moved. Then, in the moment
        // before the restore opens the clipboard, somebody copies. A count
        // compared from outside has already said "safe"; only the comparison
        // the restore makes with the clipboard open — where no other process
        // can write — still sees the copy.
        var original = Text("what was copied before the dictation");
        var copiedAsTheRestoreBegan = Text("what was copied as the restore began");
        var clipboard = new MultiFormatClipboard(original);
        clipboard.BeforeOpen = () => clipboard.ThirdPartyCopy(copiedAsTheRestoreBegan);

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Contains("changed", result.Reason);
        Assert.Equal(0, clipboard.Restores);
        AssertHolds(clipboard, copiedAsTheRestoreBegan);
    }

    [Fact]
    public async Task An_unchanged_clipboard_is_restored_even_though_the_paste_read_it()
    {
        // The other side of the guard. A paste READS the clipboard and must
        // not count as somebody else writing it, or every insert would skip
        // its restore and leave the transcript behind.
        var original = Text("what was copied before the dictation");
        var clipboard = new MultiFormatClipboard(original);

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal(string.Empty, result.Reason);
        Assert.Equal(1, clipboard.Restores);
        AssertHolds(clipboard, original);
    }

    [Fact]
    public async Task A_change_count_that_cannot_be_read_does_not_stop_the_restore()
    {
        // An unknown is not evidence anybody wrote. Skipping the restore on a
        // failed read would leave the transcript where the user's clipboard
        // was, with Success on the result.
        var original = Text("what was copied before the dictation");
        var clipboard = new MultiFormatClipboard(original) { SequenceThrows = true };

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        AssertHolds(clipboard, original);
    }

    // ─── The user's own markers survive ─────────────────────────────

    [Fact]
    public async Task A_snapshot_carrying_exclusion_markers_keeps_them()
    {
        // A password manager marks the secret it copies so clipboard history
        // and cloud sync skip it. A restore that put back only the text would
        // hand the password to Win+V history on the way back.
        var password = Text("correct horse battery staple");
        var exclude = Format(0xC0B1, "ExcludeClipboardContentFromMonitorProcessing", 0, 0, 0, 0);
        var noHistory = Format(0xC0B2, "CanIncludeInClipboardHistory", 0, 0, 0, 0);
        var noCloud = Format(0xC0B3, "CanUploadToCloudClipboard", 0, 0, 0, 0);
        var clipboard = new MultiFormatClipboard(password, exclude, noHistory, noCloud);

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        AssertHolds(clipboard, password, exclude, noHistory, noCloud);
    }

    // ─── Honest failures ────────────────────────────────────────────

    [Fact]
    public async Task A_snapshot_that_lost_a_format_restores_the_rest_and_says_so()
    {
        // Over the size budget: the giant bitmap could not be kept, the text
        // beside it could. The text goes back — it is worth having — and the
        // result does not claim the clipboard came back whole.
        var text = Text("caption");
        var clipboard = new MultiFormatClipboard(text) { Lost = ["CF_DIB (too large to keep)"] };

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.ClipboardRestoreFailed, result.Outcome);
        Assert.True(result.TextWasDelivered);
        Assert.Contains("CF_DIB", result.Reason);
        AssertHolds(clipboard, text);
    }

    [Fact]
    public async Task A_restore_the_clipboard_refuses_is_a_restore_failure()
    {
        var clipboard = new MultiFormatClipboard(Text("before")) { RestoreSucceeds = false };

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.ClipboardRestoreFailed, result.Outcome);
    }

    [Fact]
    public async Task A_whole_clipboard_reader_that_cannot_read_is_a_restore_failure_not_a_text_fallback()
    {
        // The read failing almost always means another process holds the
        // clipboard, which would stop the text read too — and a fallback that
        // sometimes worked would put back the text and drop the picture
        // beside it. Reported, not papered over.
        var clipboard = new MultiFormatClipboard(Text("before")) { CaptureFails = true };

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.ClipboardRestoreFailed, result.Outcome);
        Assert.Equal(0, clipboard.TextReads);
    }

    [Fact]
    public async Task An_undelivered_paste_still_gives_every_format_back()
    {
        // The finally path: nothing was typed, the clipboard was still
        // overwritten, and it has to come back whole.
        var image = Format(DibV5, "CF_DIBV5", 1, 2, 3);
        var html = Format(0xC0A1, "HTML Format", 4, 5, 6);
        var clipboard = new MultiFormatClipboard(image, html);
        var paste = new RecordingPaste(clipboard) { Succeeds = false };

        var result = await Insert(clipboard, paste, "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        AssertHolds(clipboard, image, html);
    }

    // ─── A write that fails half way ────────────────────────────────

    [Fact]
    public async Task A_write_that_empties_the_clipboard_and_then_fails_gives_it_back()
    {
        // The writer empties the clipboard before the text goes on. When the
        // text is then refused nothing is pasted, and the screenshot that was
        // there must not stay gone under an outcome that says nothing changed.
        var image = Format(DibV5, "CF_DIBV5", 1, 2, 3);
        var html = Format(0xC0A1, "HTML Format", 4, 5, 6);
        var clipboard = new MultiFormatClipboard(image, html);
        clipboard.RefusedWrite = clipboard.Emptied;
        var paste = new RecordingPaste(clipboard);

        var result = await Insert(clipboard, paste, "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        Assert.Equal("the clipboard would not take the text", result.Reason);
        Assert.False(paste.Sent);
        Assert.Equal(1, clipboard.Restores);
        AssertHolds(clipboard, image, html);
    }

    [Fact]
    public async Task A_write_that_fails_before_touching_the_clipboard_writes_nothing_back()
    {
        // The usual failure: another process held the clipboard, so nothing
        // was emptied. The user's content is still there, and writing it
        // again would be a needless write over the user's own clipboard.
        var original = Text("what was copied before the dictation");
        var clipboard = new MultiFormatClipboard(original);
        clipboard.RefusedWrite = () => { };

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        Assert.Equal("the clipboard would not take the text", result.Reason);
        Assert.Equal(0, clipboard.Restores);
        AssertHolds(clipboard, original);
    }

    [Fact]
    public async Task A_copy_made_while_the_write_was_failing_is_not_overwritten()
    {
        // The count moved, but because another process wrote — very likely
        // the same process whose hold on the clipboard made this write fail.
        // What it wrote is newer than the snapshot, and only the clipboard
        // not being empty tells it apart from an emptying of the insert's own.
        var original = Text("what was copied before the dictation");
        var theirs = Text("what the other process wrote");
        var clipboard = new MultiFormatClipboard(original);
        clipboard.RefusedWrite = () => clipboard.ThirdPartyCopy(theirs);

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        Assert.Equal(0, clipboard.Restores);
        AssertHolds(clipboard, theirs);
    }

    [Fact]
    public async Task A_failed_write_whose_emptying_could_not_be_undone_says_so()
    {
        var clipboard = new MultiFormatClipboard(Text("before")) { RestoreSucceeds = false };
        clipboard.RefusedWrite = clipboard.Emptied;

        var result = await Insert(clipboard, new RecordingPaste(clipboard), "the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        Assert.StartsWith("the clipboard would not take the text; the clipboard was also not restored", result.Reason);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static Task<FocusedInsertionResult> Insert(
        MultiFormatClipboard clipboard, RecordingPaste paste, string text) =>
        new FocusedTextInsertion(
                new Resolver(), clipboard, paste,
                NullLogger<FocusedTextInsertion>.Instance,
                pasteSettleDelay: TimeSpan.Zero)
            .InsertAsync(text);

    private static ClipboardFormatData Format(uint id, string name, params byte[] data) => new(id, name, data);

    private static ClipboardFormatData Text(string text) =>
        new(UnicodeText, "CF_UNICODETEXT", Encoding.Unicode.GetBytes(text + '\0'));

    /// <summary>Same formats, same order, same bytes — not merely "present".</summary>
    private static void AssertHolds(MultiFormatClipboard clipboard, params ClipboardFormatData[] expected)
    {
        Assert.Equal(
            expected.Select(f => (f.Id, f.Name, Convert.ToHexString(f.Data))),
            clipboard.Current.Select(f => (f.Id, f.Name, Convert.ToHexString(f.Data))));
    }

    private sealed class Resolver : IFocusedApplicationResolver
    {
        public FocusedApplicationResult Resolve() =>
            FocusedApplicationResult.Resolved("notepad", 4242, "Untitled - Notepad");
    }

    /// <summary>
    /// A clipboard with several formats and a change count, as Windows has.
    /// Every write — ours, a restore, a third party's — bumps the count, and
    /// a read does not, which is the property the settle guard relies on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implements the text-only read too, so a regression that went back to
    /// it is visible: <see cref="TextReads"/> counts its use, and the text it
    /// answers is exactly the half-picture the old restore put back.
    /// </para>
    /// <para>
    /// <see cref="Restore"/> keeps the Win32 contract: it checks the
    /// condition after the point where the clipboard would be opened, which
    /// <see cref="BeforeOpen"/> lets a test write into, and
    /// <see cref="Restores"/> counts only restores that wrote.
    /// <see cref="RefusedWrite"/> makes the transcript write fail, after doing
    /// whatever a failing write did to the clipboard.
    /// </para>
    /// </remarks>
    private sealed class MultiFormatClipboard(params ClipboardFormatData[] initial)
        : IClipboard, IClipboardTextSnapshot, IClipboardSnapshot
    {
        public List<ClipboardFormatData> Current { get; private set; } = [.. initial];

        public uint Sequence { get; private set; } = 100;

        public bool RestoreSucceeds { get; init; } = true;

        public bool CaptureFails { get; init; }

        public bool SequenceThrows { get; init; }

        public string[] Lost { get; init; } = [];

        public int Restores { get; private set; }

        public int TextReads { get; private set; }

        /// <summary>Runs as a restore begins, before the clipboard is open:
        /// the last moment another process can still write it.</summary>
        public Action? BeforeOpen { get; set; }

        /// <summary>When set, the transcript write runs this instead and
        /// returns false.</summary>
        public Action? RefusedWrite { get; set; }

        public bool TrySet(string text)
        {
            if (RefusedWrite is { } refused)
            {
                refused();
                return false;
            }

            Current = [Text(text)];
            Sequence++;
            return true;
        }

        public bool TryRead(out string? text)
        {
            TextReads++;
            text = TextOf(Current);
            return true;
        }

        public ClipboardContents? Capture() =>
            CaptureFails ? null : new ClipboardContents(Current.ToArray(), Lost);

        public SnapshotRestoreResult Restore(ClipboardContents contents, ClipboardRestoreCondition? condition)
        {
            BeforeOpen?.Invoke();

            // From here the clipboard is open: nobody else can write it.
            if (condition is { } expected
                && (Sequence != expected.Sequence || (expected.HoldsNothing && Current.Count > 0)))
            {
                return SnapshotRestoreResult.Changed;
            }

            if (!RestoreSucceeds)
            {
                return SnapshotRestoreResult.Failed;
            }

            Restores++;
            Current = [.. contents.Formats];
            Sequence++;
            return SnapshotRestoreResult.Restored;
        }

        public uint SequenceNumber =>
            SequenceThrows ? throw new InvalidOperationException("no window station access") : Sequence;

        public void ThirdPartyCopy(params ClipboardFormatData[] formats)
        {
            Current = [.. formats];
            Sequence++;
        }

        /// <summary>What the writer's EmptyClipboard does: everything gone,
        /// and a change counted.</summary>
        public void Emptied()
        {
            Current = [];
            Sequence++;
        }

        public static string? TextOf(IEnumerable<ClipboardFormatData> formats) =>
            formats.FirstOrDefault(f => f.Id == UnicodeText) is { } text
                ? Encoding.Unicode.GetString(text.Data).TrimEnd('\0')
                : null;
    }

    /// <summary>
    /// Captures what a real target would have read when the keystroke went,
    /// and can play a third party writing the clipboard straight after.
    /// </summary>
    private sealed class RecordingPaste(MultiFormatClipboard clipboard) : IPasteKeystroke
    {
        public bool Succeeds { get; init; } = true;

        public Action? AfterSend { get; init; }

        public string? TextWhenSent { get; private set; }

        public bool Sent { get; private set; }

        public bool TrySend()
        {
            Sent = true;
            TextWhenSent = MultiFormatClipboard.TextOf(clipboard.Current);
            AfterSend?.Invoke();
            return Succeeds;
        }
    }
}
