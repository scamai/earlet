using System.Text;
using Dictation.Platform.Insert;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Infrastructure.Tests;

/// <summary>
/// dictation M1 — the half of <see cref="Win32ReadableClipboard"/> that can be
/// held to account without a live desktop.
///
/// <para>
/// This class has two methods and they are not symmetrical.
/// <c>TryRead</c> is Win32 all the way down: its very first statement is a
/// <c>IsClipboardFormatAvailable</c> P/Invoke, so there is no branch of it a
/// test can reach without a real clipboard on a real Windows session, and no
/// seam was left to inject one. <c>TrySet</c> is the opposite — it forwards to
/// the injected <see cref="IClipboard"/> and does nothing else. That
/// asymmetry is the class's central design decision, stated in its own
/// remarks: the write is not re-derived here because <c>Win32Clipboard</c>'s
/// <c>GlobalAlloc</c> ownership handoff is the subtle part of that file and a
/// copy would be a second place to get it wrong.
/// </para>
///
/// <para>
/// So this file tests the decision rather than the plumbing. "Forwards and
/// adds nothing" is a claim that can silently stop being true — a retry
/// slipped in to be helpful, a guard that swallows the empty string, a
/// success invented when the writer said no — and each of those turns
/// dictation's most user-visible promise into a lie, because the write this
/// forwards is the one that puts the user's own clipboard back afterwards.
/// </para>
///
/// <para>
/// What is deliberately <em>not</em> here is a <c>TryRead</c> test. Faking one
/// would mean asserting against a stub of the type under test, which is the
/// one thing a test of this class cannot honestly do; a real round-trip
/// belongs to the clipboard-restoration device gate in VERIFY.md, run on a
/// physical Windows box where there is a clipboard to round-trip through.
/// </para>
///
/// <para>
/// The whole-clipboard snapshot is split the same way. Its Win32 half —
/// listing, rendering, writing back — is device-only; its decisions are two
/// pure functions, <see cref="ClipboardFormatSnapshot.Classify"/> and
/// <see cref="ClipboardFormatSnapshot.Build"/>, and those are tested here
/// against a fake reader that records what it was asked to read.
/// </para>
/// </summary>
public sealed class Win32ReadableClipboardTests
{
    // ─── Delegation ─────────────────────────────────────────────────

    [Fact]
    public void TrySet_hands_the_text_to_the_clipboard_it_was_given()
    {
        var writer = new InMemoryClipboard();
        var clipboard = Clipboard(writer);

        clipboard.TrySet("the dictated sentence");

        // Exactly once, and byte-for-byte. A wrapper that trimmed, normalised
        // or re-encoded on the way through would put something other than the
        // user's words in front of them, and the transcript is the product.
        Assert.Equal(new[] { "the dictated sentence" }, writer.Writes);
    }

    [Fact]
    public void TrySet_reports_a_write_the_clipboard_accepted()
    {
        var clipboard = Clipboard(new InMemoryClipboard { Succeeds = true });

        Assert.True(clipboard.TrySet("hello"));
    }

    [Fact]
    public void TrySet_reports_a_write_the_clipboard_refused()
    {
        // The direction that actually matters. OpenClipboard fails while
        // another process holds the global lock, and FocusedTextInsertion
        // reads a false here as "the paste never happened" — so a wrapper that
        // optimistically returned true would have dictation report delivery of
        // text that was never placed anywhere.
        var clipboard = Clipboard(new InMemoryClipboard { Succeeds = false });

        Assert.False(clipboard.TrySet("hello"));
    }

    [Fact]
    public void TrySet_does_not_retry_a_refused_write()
    {
        // One attempt, no retry, matching the write side it wraps. A retry
        // added here would be invisible to the caller and would keep dictated
        // text sitting on the clipboard while a second contender fought for
        // the lock.
        var writer = new InMemoryClipboard { Succeeds = false };
        var clipboard = Clipboard(writer);

        clipboard.TrySet("hello");

        Assert.Equal(1, writer.Calls);
    }

    [Fact]
    public void TrySet_passes_the_empty_string_through_untouched()
    {
        // A forwarder has no opinion about what it forwards. An empty
        // string "helpfully" skipped here would be a write the caller
        // believes happened — and the clipboard would still hold whatever
        // was there before, the dictated sentence included. (An empty
        // clipboard is restored through IClipboardSnapshot, as an emptied
        // clipboard, not through this write.)
        var writer = new InMemoryClipboard();
        var clipboard = Clipboard(writer);

        clipboard.TrySet(string.Empty);

        Assert.Equal(new[] { string.Empty }, writer.Writes);
    }

    // ─── Construction ───────────────────────────────────────────────

    [Fact]
    public void A_missing_writer_is_refused_at_construction()
    {
        // Refused here rather than at the first TrySet. This object is built
        // once, at composition time, and a null that only surfaces on the
        // first hotkey press turns a wiring mistake into a crash in front of
        // the user mid-sentence.
        var ex = Assert.Throws<ArgumentNullException>(
            () => new Win32ReadableClipboard(
                null!, NullLogger<Win32ReadableClipboard>.Instance));

        Assert.Equal("writer", ex.ParamName);
    }

    [Fact]
    public void A_missing_logger_is_refused_at_construction()
    {
        // The logger is not decoration here: every failure path in TryRead
        // reports itself only by logging, so a null one would make an
        // unreadable clipboard indistinguishable from an empty one in a crash
        // report.
        var ex = Assert.Throws<ArgumentNullException>(
            () => new Win32ReadableClipboard(new InMemoryClipboard(), null!));

        Assert.Equal("logger", ex.ParamName);
    }

    // ─── The seam FocusedTextInsertion looks for ────────────────────

    [Fact]
    public void It_answers_to_both_the_write_seam_and_the_read_seam()
    {
        var clipboard = Clipboard(new InMemoryClipboard());

        // FocusedTextInsertion does not ask for IClipboardTextSnapshot in its
        // constructor; it takes an IClipboard and duck-checks
        // `_clipboard is not IClipboardTextSnapshot readable`, treating a
        // clipboard it cannot read as one it cannot restore. So dropping
        // either interface from this class is a compile-clean change that
        // turns every single insert into ClipboardRestoreFailed — the exact
        // symptom this class was written to remove.
        Assert.IsAssignableFrom<IClipboard>(clipboard);
        Assert.IsAssignableFrom<IClipboardTextSnapshot>(clipboard);

        // And the whole-clipboard seam, discovered the same way. Without it
        // the insert falls back to the text read, and a copied screenshot is
        // lost to every dictation again — still compile-clean.
        Assert.IsAssignableFrom<IClipboardSnapshot>(clipboard);
    }

    // ─── The writer it composes: what one write puts on the clipboard ──

    // Win32Clipboard is the IClipboard this class forwards every write to in
    // the app. Its TrySet opens the clipboard, hands the open session to
    // Win32Clipboard.Write, and closes it. Two things are held to account
    // here: the list a write is made of — Win32Clipboard.Plan — and, further
    // down, that Write actually puts every entry of it on the clipboard. The
    // list alone is not enough: a plan nobody executes passes every test of
    // the plan. Whether Windows honours the markers is a device check
    // (VERIFY.md), not something a Linux test can see.

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("CanIncludeInClipboardHistory")]
    [InlineData("CanUploadToCloudClipboard")]
    public void Every_write_carries_the_marker_that_keeps_it_out_of_history(string marker)
    {
        var plan = Win32Clipboard.Plan("the dictated sentence");

        // Exactly once, by its documented name, and as a DWORD of zero. Zero
        // is the meaning for the two Can… formats — a one would ASK for
        // history and cloud sync — and four zero bytes rather than none for
        // the exclusion format, because a zero-length block cannot be locked.
        var write = Assert.Single(plan, w => w.Name == marker);
        Assert.Equal(new byte[4], write.Payload);
    }

    [Fact]
    public void The_text_goes_first_and_whole()
    {
        var plan = Win32Clipboard.Plan("héllo");

        // First, because TrySet treats the first entry as the content — the
        // one write whose failure means "do not paste" — and the markers after
        // it as best effort. UTF-16 with its terminator, which is what
        // CF_UNICODETEXT (13) is defined to hold.
        Assert.Equal(13u, plan[0].Format);
        Assert.Null(plan[0].Name);
        Assert.Equal("héllo\0", System.Text.Encoding.Unicode.GetString(plan[0].Payload));
    }

    [Fact]
    public void A_write_is_the_text_and_the_three_markers_and_nothing_else()
    {
        var plan = Win32Clipboard.Plan("x");

        // Pins the whole list, so a fourth format slipped in later — or a
        // marker renamed into a near miss the history service will not read —
        // is a visible change to this file rather than a quiet one.
        Assert.Equal(
            new[]
            {
                "CF_UNICODETEXT",
                "ExcludeClipboardContentFromMonitorProcessing",
                "CanIncludeInClipboardHistory",
                "CanUploadToCloudClipboard",
            },
            plan.Select(w => w.Name ?? "CF_UNICODETEXT"));
    }

    // ─── The writer's session: what one write actually does ────────

    // Win32Clipboard.Write against a session that records every call. It is
    // the code TrySet runs between OpenClipboard and CloseClipboard; only the
    // Win32 primitives themselves (one call each) are left to the device.

    [Fact]
    public void A_write_empties_once_then_puts_the_text_and_every_marker_on_in_plan_order()
    {
        var plan = Win32Clipboard.Plan("the dictated sentence");
        var session = new RecordingSession();

        Assert.True(Write(plan, session));

        // The markers have to be ON the clipboard, not just in the plan: the
        // history service reads what was placed, and a marker the plan names
        // but the session never sets keeps nothing out of Win+V.
        Assert.Equal(1, session.Count("Empty"));
        Assert.Equal(
            new[]
            {
                "CF_UNICODETEXT",
                "ExcludeClipboardContentFromMonitorProcessing",
                "CanIncludeInClipboardHistory",
                "CanUploadToCloudClipboard",
            },
            session.Placed.Select(p => p.Name));
        Assert.Equal(plan.Select(w => w.Payload), session.Placed.Select(p => p.Payload));
        session.AssertEveryBlockTakenOrFreedOnce();
    }

    [Fact]
    public void The_text_is_ready_before_the_clipboard_is_emptied()
    {
        var plan = Win32Clipboard.Plan("x");
        var session = new RecordingSession();

        Write(plan, session);

        // Allocated and filled first, so the only step between emptying the
        // user's clipboard and putting the text on it is the hand-over.
        Assert.Equal(new[] { "Allocate", "Empty", "Set" }, session.Calls.Take(3).Select(c => c.Op));
        Assert.Equal(plan[0].Payload, session.Calls[0].Payload);
    }

    [Fact]
    public void A_text_that_cannot_be_allocated_leaves_the_clipboard_untouched()
    {
        // The caller reads false as "nothing changed" and does not paste. That
        // is only true if the user's clipboard was never emptied.
        var session = new RecordingSession { RefuseAllocation = 0 };

        Assert.False(Write(Win32Clipboard.Plan("x"), session));

        Assert.Equal(0, session.Count("Empty"));
        Assert.Empty(session.Placed);
        session.AssertEveryBlockTakenOrFreedOnce();
    }

    [Fact]
    public void A_clipboard_that_cannot_be_emptied_is_refused_and_the_text_freed()
    {
        var session = new RecordingSession { EmptyFails = true };

        Assert.False(Write(Win32Clipboard.Plan("x"), session));

        Assert.Equal(0, session.Count("Set"));
        session.AssertEveryBlockTakenOrFreedOnce();
    }

    [Fact]
    public void A_refused_text_is_a_failed_write_and_no_marker_is_attempted()
    {
        // Markers on an emptied clipboard with no text would mark nothing, and
        // false already tells the caller not to paste.
        var session = new RecordingSession { Refuse = { "CF_UNICODETEXT" } };

        Assert.False(Write(Win32Clipboard.Plan("x"), session));

        Assert.Equal(0, session.Count("Register"));
        Assert.Equal(1, session.Count("Set"));
        Assert.Empty(session.Placed);
        session.AssertEveryBlockTakenOrFreedOnce();
    }

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("CanIncludeInClipboardHistory")]
    [InlineData("CanUploadToCloudClipboard")]
    public void A_refused_marker_does_not_fail_the_write_and_the_rest_are_still_placed(string refused)
    {
        // True, because the text IS on the clipboard and false would tell the
        // caller it is not. And the other markers still go on: each one closes
        // a different door, so losing one is no reason to lose the others.
        var session = new RecordingSession { Refuse = { refused } };

        Assert.True(Write(Win32Clipboard.Plan("x"), session));

        Assert.Equal(AllBut(refused), session.Placed.Select(p => p.Name));
        session.AssertEveryBlockTakenOrFreedOnce();
    }

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("CanIncludeInClipboardHistory")]
    [InlineData("CanUploadToCloudClipboard")]
    public void A_marker_that_cannot_be_registered_is_skipped_and_the_rest_are_still_placed(string missing)
    {
        var session = new RecordingSession { Unregistered = { missing } };

        Assert.True(Write(Win32Clipboard.Plan("x"), session));

        Assert.Equal(AllBut(missing), session.Placed.Select(p => p.Name));
        // Nothing allocated for a format with no number to put it under.
        Assert.Equal(3, session.Count("Allocate"));
        session.AssertEveryBlockTakenOrFreedOnce();
    }

    [Fact]
    public void A_marker_that_cannot_be_allocated_is_skipped_and_the_rest_are_still_placed()
    {
        // Allocation 0 is the text, 1 the first marker, 2 the second.
        var session = new RecordingSession { RefuseAllocation = 2 };

        Assert.True(Write(Win32Clipboard.Plan("x"), session));

        Assert.Equal(AllBut("CanIncludeInClipboardHistory"), session.Placed.Select(p => p.Name));
        session.AssertEveryBlockTakenOrFreedOnce();
    }

    private static bool Write(IReadOnlyList<ClipboardWrite> plan, RecordingSession session) =>
        Win32Clipboard.Write(plan, session, NullLogger.Instance);

    private static string[] AllBut(string marker) =>
        new[]
        {
            "CF_UNICODETEXT",
            "ExcludeClipboardContentFromMonitorProcessing",
            "CanIncludeInClipboardHistory",
            "CanUploadToCloudClipboard",
        }.Where(name => name != marker).ToArray();

    // ─── The whole-clipboard snapshot: what is read, kept and lost ──

    [Theory]
    [InlineData(2u)]      // CF_BITMAP — an HBITMAP
    [InlineData(3u)]      // CF_METAFILEPICT — memory holding a metafile handle
    [InlineData(9u)]      // CF_PALETTE
    [InlineData(14u)]     // CF_ENHMETAFILE — an HENHMETAFILE
    [InlineData(0x80u)]   // CF_OWNERDISPLAY
    [InlineData(0x82u)]   // CF_DSPBITMAP
    [InlineData(0x83u)]   // CF_DSPMETAFILEPICT
    [InlineData(0x8Eu)]   // CF_DSPENHMETAFILE
    [InlineData(0x200u)]  // first private
    [InlineData(0x2FFu)]  // last private
    [InlineData(0x300u)]  // first GDI object
    [InlineData(0x3FFu)]  // last GDI object
    public void A_handle_is_never_copied_as_if_it_were_memory(uint format)
    {
        // Its "bytes" are a handle into GDI or another process. Written back,
        // they hand a pasting application a pointer to nothing.
        Assert.Equal(
            ClipboardFormatTreatment.NotMemory,
            ClipboardFormatSnapshot.Classify(format, Name(format), []));
    }

    [Theory]
    [InlineData(13u, "CF_UNICODETEXT")]
    [InlineData(15u, "CF_HDROP")]
    [InlineData(16u, "CF_LOCALE")]
    [InlineData(8u, "CF_DIB")]
    [InlineData(17u, "CF_DIBV5")]
    [InlineData(0x81u, "CF_DSPTEXT")]
    [InlineData(0xC001u, "HTML Format")]
    [InlineData(0xC002u, "Rich Text Format")]
    [InlineData(0xC003u, "PNG")]
    [InlineData(0xC004u, "FileGroupDescriptorW")]
    [InlineData(0xC005u, "ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData(0xC006u, "CanIncludeInClipboardHistory")]
    [InlineData(0xC007u, "CanUploadToCloudClipboard")]
    public void Memory_formats_are_copied_including_the_users_own_history_markers(uint format, string name)
    {
        // The last three matter most: a password manager marks its secret so
        // history and cloud sync skip it, and a restore that dropped the marks
        // would put the password into Win+V history on the way back.
        Assert.Equal(
            ClipboardFormatTreatment.Copy,
            ClipboardFormatSnapshot.Classify(format, name, []));
    }

    [Theory]
    [InlineData("Link Source")]
    [InlineData("Link Source Descriptor")]
    [InlineData("ObjectLink")]
    [InlineData("OwnerLink")]
    [InlineData("Native")]
    [InlineData("Embed Source")]
    [InlineData("embed source")]
    public void OLE_link_and_embedding_formats_are_never_read(string name)
    {
        // Rendering these makes Word bookmark the user's document, or raises
        // dialogs in Outlook's editor. Reading them IS the harm, so they are
        // classified away before any read happens.
        var reader = new FakeReader { [0xC010] = [1, 2, 3] };

        var contents = ClipboardFormatSnapshot.Build([new(0xC010, name)], reader);

        Assert.Equal(
            ClipboardFormatTreatment.UnsafeToRead,
            ClipboardFormatSnapshot.Classify(0xC010, name, []));
        Assert.Empty(reader.Asked);
        Assert.Empty(contents.Formats);
    }

    [Fact]
    public void A_screenshot_keeps_one_bitmap_and_lets_Windows_rebuild_the_rest()
    {
        // What a screenshot lists: the GDI bitmap that was placed, then the
        // two device-independent forms Windows synthesised from it. One DIB
        // is the picture; Windows makes the other and the HBITMAP again from
        // it, so copying both would hold the image twice for nothing. The V5
        // is the one kept, because it is the one that can carry alpha.
        var reader = new FakeReader { [8] = Bytes(40), [17] = Bytes(124) };

        var contents = ClipboardFormatSnapshot.Build(
            [new(2, "CF_BITMAP"), new(8, "CF_DIB"), new(17, "CF_DIBV5")], reader);

        Assert.Equal(new[] { "CF_DIBV5" }, contents.Formats.Select(f => f.Name));
        Assert.Equal(new[] { "CF_BITMAP", "CF_DIB" }, contents.Omitted);
        Assert.Empty(contents.Lost);
        Assert.DoesNotContain(8u, reader.Asked);
        Assert.DoesNotContain(2u, reader.Asked);
    }

    [Fact]
    public void A_picture_keeps_its_alpha_when_its_owner_listed_a_plain_bitmap_first()
    {
        // Some editors and browsers place a plain DIB for older programs and
        // then the alpha V5. Keeping whichever came first kept the DIB, and
        // the V5 Windows rebuilt from it after the restore had no
        // transparency — with nothing reporting it.
        var reader = new FakeReader { [8] = Bytes(40), [17] = Bytes(124) };

        var contents = ClipboardFormatSnapshot.Build(
            [new(8, "CF_DIB"), new(17, "CF_DIBV5")], reader);

        Assert.Equal(new[] { "CF_DIBV5" }, contents.Formats.Select(f => f.Name));
        Assert.Equal(new[] { "CF_DIB" }, contents.Omitted);
        Assert.DoesNotContain(8u, reader.Asked);
    }

    [Fact]
    public void The_plain_bitmap_is_kept_in_its_place_when_the_alpha_one_could_not_be_read()
    {
        // The rebuild rule only holds when the V5 was actually kept; and the
        // DIB, decided last, still goes back where its owner listed it.
        var reader = new FakeReader { [8] = Bytes(40), [17] = null, [0xC003] = Bytes(9) };

        var contents = ClipboardFormatSnapshot.Build(
            [new(8, "CF_DIB"), new(17, "CF_DIBV5"), new(0xC003, "PNG")], reader);

        Assert.Equal(new[] { "CF_DIB", "PNG" }, contents.Formats.Select(f => f.Name));
        Assert.Empty(contents.Lost);
    }

    [Fact]
    public void Text_keeps_Unicode_and_its_locale_and_lets_Windows_rebuild_ANSI_and_OEM()
    {
        // An ANSI program lists CF_TEXT BEFORE the Unicode Windows made from
        // it, which is why ANSI is decided in a second pass. Unicode is kept
        // in its listed place, so the restored order matches the original.
        var reader = new FakeReader
        {
            [1] = Bytes(6), [13] = Bytes(12), [16] = Bytes(4), [7] = Bytes(6),
        };

        var contents = ClipboardFormatSnapshot.Build(
            [new(1, "CF_TEXT"), new(13, "CF_UNICODETEXT"), new(16, "CF_LOCALE"), new(7, "CF_OEMTEXT")],
            reader);

        Assert.Equal(new[] { "CF_UNICODETEXT", "CF_LOCALE" }, contents.Formats.Select(f => f.Name));
        Assert.Equal(new[] { "CF_TEXT", "CF_OEMTEXT" }, contents.Omitted);
        Assert.Empty(contents.Lost);
    }

    [Fact]
    public void ANSI_text_is_kept_when_Unicode_could_not_be_read()
    {
        // The rebuild rule only holds when the source was actually kept.
        var reader = new FakeReader { [1] = Bytes(6), [13] = null };

        var contents = ClipboardFormatSnapshot.Build(
            [new(1, "CF_TEXT"), new(13, "CF_UNICODETEXT")], reader);

        Assert.Equal(new[] { "CF_TEXT" }, contents.Formats.Select(f => f.Name));
    }

    [Fact]
    public void ANSI_text_kept_in_the_second_pass_goes_back_in_its_listed_place()
    {
        // CF_TEXT is decided after everything else, but it was listed first,
        // and a pasting application that understands both takes the first.
        // Appending it in the order it was decided would put the HTML ahead
        // of the text the owner put ahead of it.
        var reader = new FakeReader { [1] = Bytes(6), [13] = null, [0xC001] = Bytes(9) };

        var contents = ClipboardFormatSnapshot.Build(
            [new(1, "CF_TEXT"), new(13, "CF_UNICODETEXT"), new(0xC001, "HTML Format")], reader);

        Assert.Equal(new[] { "CF_TEXT", "HTML Format" }, contents.Formats.Select(f => f.Name));
    }

    [Fact]
    public void Kept_formats_keep_their_bytes_and_their_listed_order()
    {
        // A pasting application takes the FIRST format it understands, so the
        // order is part of what the user copied.
        var html = Encoding.UTF8.GetBytes("<b>hi</b>");
        var rtf = Encoding.ASCII.GetBytes(@"{\rtf1 hi}");
        var text = Encoding.Unicode.GetBytes("hi\0");
        var reader = new FakeReader { [0xC001] = html, [13] = text, [0xC002] = rtf };

        var contents = ClipboardFormatSnapshot.Build(
            [new(0xC001, "HTML Format"), new(13, "CF_UNICODETEXT"), new(0xC002, "Rich Text Format")],
            reader);

        Assert.Equal(new[] { 0xC001u, 13u, 0xC002u }, contents.Formats.Select(f => f.Id));
        Assert.Equal(html, contents.Formats[0].Data);
        Assert.Equal(text, contents.Formats[1].Data);
        Assert.Equal(rtf, contents.Formats[2].Data);
    }

    [Fact]
    public void A_format_over_the_size_budget_is_lost_and_the_rest_is_kept()
    {
        var reader = new FakeReader { [13] = Bytes(10), [0xC003] = Bytes(1000) };

        var contents = ClipboardFormatSnapshot.Build(
            [new(13, "CF_UNICODETEXT"), new(0xC003, "PNG")], reader, maxBytes: 100);

        Assert.Equal(new[] { "CF_UNICODETEXT" }, contents.Formats.Select(f => f.Name));
        Assert.Equal(new[] { "PNG (too large to keep)" }, contents.Lost);
        // Sized, never copied: the budget exists so a gigabyte is not
        // duplicated into this process.
        Assert.DoesNotContain(0xC003u, reader.Copied);
    }

    [Fact]
    public void The_budget_is_for_everything_copied_not_for_each_format()
    {
        // Each of these fits on its own; together they do not. A per-format
        // check would copy both, and the budget exists to bound what one
        // snapshot holds, not what one format does.
        var reader = new FakeReader { [13] = Bytes(60), [0xC003] = Bytes(60) };

        var contents = ClipboardFormatSnapshot.Build(
            [new(13, "CF_UNICODETEXT"), new(0xC003, "PNG")], reader, maxBytes: 100);

        Assert.Equal(new[] { "CF_UNICODETEXT" }, contents.Formats.Select(f => f.Name));
        Assert.Equal(new[] { "PNG (too large to keep)" }, contents.Lost);
        Assert.DoesNotContain(0xC003u, reader.Copied);
    }

    [Fact]
    public void A_bitmap_over_the_budget_is_not_lost_when_its_twin_fitted()
    {
        // The DIBV5 is listed first and is too big; the plain DIB fits, and
        // Windows rebuilds the DIBV5 from it. Calling that a loss would tell
        // the user their picture is gone when it is not.
        var reader = new FakeReader { [17] = Bytes(124), [8] = Bytes(40) };

        var contents = ClipboardFormatSnapshot.Build(
            [new(17, "CF_DIBV5"), new(8, "CF_DIB")], reader, maxBytes: 100);

        Assert.Equal(new[] { "CF_DIB" }, contents.Formats.Select(f => f.Name));
        Assert.Empty(contents.Lost);
    }

    [Fact]
    public void A_clipboard_of_which_nothing_could_be_kept_is_lost_not_empty()
    {
        // A vector picture alone: both metafile forms are handles. Restoring
        // that as an empty clipboard and calling it a success would be the old
        // "nothing to put back" bug in a new place.
        var reader = new FakeReader();

        var contents = ClipboardFormatSnapshot.Build(
            [new(14, "CF_ENHMETAFILE"), new(3, "CF_METAFILEPICT")], reader);

        Assert.Empty(contents.Formats);
        Assert.Equal(new[] { "CF_ENHMETAFILE", "CF_METAFILEPICT" }, contents.Lost);
    }

    [Fact]
    public void An_empty_clipboard_is_an_empty_snapshot_with_nothing_lost()
    {
        var contents = ClipboardFormatSnapshot.Build([], new FakeReader());

        Assert.Empty(contents.Formats);
        Assert.Empty(contents.Lost);
    }

    [Fact]
    public void A_format_with_no_data_is_left_out_unless_its_presence_is_the_point()
    {
        // Visual Studio and Scintilla mark a whole-line copy by the format's
        // presence alone. Dropping it would make the next paste insert a
        // fragment where a line was copied. Any other null is an owner that
        // would not render: no paste could have had it, so it is not lost.
        var reader = new FakeReader { [13] = Bytes(4), [0xC020] = null, [0xC021] = null };

        var contents = ClipboardFormatSnapshot.Build(
            [new(13, "CF_UNICODETEXT"), new(0xC020, "MSDEVLineSelect"), new(0xC021, "Some Owner Format")],
            reader);

        Assert.Equal(new[] { "CF_UNICODETEXT", "MSDEVLineSelect" }, contents.Formats.Select(f => f.Name));
        Assert.Empty(contents.Formats[1].Data);
        Assert.Contains("Some Owner Format (no data)", contents.Omitted);
        Assert.Empty(contents.Lost);
    }

    // ─── The restore: compared, emptied and written in one open session ──

    // ClipboardFormatSnapshot.Restore against a session that records every
    // call. It is the code that runs between OpenClipboard and CloseClipboard
    // when a snapshot goes back; only the single Win32 calls behind each
    // member are left to the device.

    [Fact]
    public void A_restore_compares_the_count_with_the_clipboard_open_before_it_empties_anything()
    {
        // Somebody copied after the insert last looked. With the clipboard
        // open nobody else can write, so this comparison cannot be overtaken
        // — but only if it comes before the emptying.
        var session = new RecordingRestoreSession { Sequence = 8 };

        var result = Restore(Snapshot(TextFormat("before")), new ClipboardRestoreCondition(7), session);

        Assert.Equal(SnapshotRestoreResult.Changed, result);
        Assert.Equal(new[] { "Sequence" }, session.Calls);
        Assert.Empty(session.Placed);
    }

    [Fact]
    public void A_restore_whose_count_still_matches_empties_once_then_writes_every_format_in_order()
    {
        var text = TextFormat("before");
        var html = new ClipboardFormatData(0xC001, "HTML Format", [1, 2, 3]);
        var session = new RecordingRestoreSession { Sequence = 7 };

        var result = Restore(Snapshot(text, html), new ClipboardRestoreCondition(7), session);

        Assert.Equal(SnapshotRestoreResult.Restored, result);
        // The history markers come after these; they are pinned further down.
        Assert.Equal(new[] { "Sequence", "Empty", "Put", "Put" }, session.Calls.Take(4));
        Assert.Equal(new[] { (13u, text.Data), (0xC001u, html.Data) }, session.Copies);
    }

    [Fact]
    public void A_restore_with_no_condition_does_not_look_at_the_count()
    {
        // No count was read after the write, which leaves the restore
        // unconditional — an unknown is not evidence somebody else wrote.
        var session = new RecordingRestoreSession { Sequence = 99 };

        Assert.Equal(SnapshotRestoreResult.Restored, Restore(Snapshot(TextFormat("before")), null, session));
        Assert.DoesNotContain("Sequence", session.Calls);
    }

    [Fact]
    public void An_empty_snapshot_is_restored_as_an_emptied_clipboard()
    {
        var session = new RecordingRestoreSession { Sequence = 7 };

        Assert.Equal(
            SnapshotRestoreResult.Restored,
            Restore(ClipboardContents.Empty, new ClipboardRestoreCondition(7), session));
        Assert.Equal(new[] { "Sequence", "Empty" }, session.Calls);
    }

    [Fact]
    public void Undoing_a_failed_write_writes_nothing_over_a_clipboard_that_holds_something()
    {
        // The count still stands, but the clipboard is not empty: the failed
        // write never emptied it, or another process wrote it in the same
        // moment. Either way there is nothing of the insert's to undo.
        var session = new RecordingRestoreSession { Sequence = 7, Formats = 2 };

        var result = Restore(
            Snapshot(TextFormat("before")), new ClipboardRestoreCondition(7, HoldsNothing: true), session);

        Assert.Equal(SnapshotRestoreResult.Changed, result);
        Assert.Equal(new[] { "Sequence", "FormatCount" }, session.Calls);
    }

    [Fact]
    public void Undoing_a_failed_write_that_left_the_clipboard_empty_puts_the_snapshot_back()
    {
        var text = TextFormat("before");
        var session = new RecordingRestoreSession { Sequence = 7, Formats = 0 };

        var result = Restore(Snapshot(text), new ClipboardRestoreCondition(7, HoldsNothing: true), session);

        Assert.Equal(SnapshotRestoreResult.Restored, result);
        Assert.Equal(new[] { "Sequence", "FormatCount", "Empty", "Put" }, session.Calls.Take(4));
        Assert.Equal(new[] { (13u, text.Data) }, session.Copies);
    }

    [Fact]
    public void A_clipboard_that_cannot_be_emptied_gets_nothing_written()
    {
        var session = new RecordingRestoreSession { Sequence = 7, EmptyFails = true };

        Assert.Equal(
            SnapshotRestoreResult.Failed,
            Restore(Snapshot(TextFormat("before")), new ClipboardRestoreCondition(7), session));
        Assert.Empty(session.Placed);
    }

    [Fact]
    public void A_refused_format_fails_the_restore_and_the_rest_still_go_back()
    {
        // The text back without its picture beats neither back.
        var image = new ClipboardFormatData(17, "CF_DIBV5", [1, 2, 3]);
        var text = TextFormat("caption");
        var session = new RecordingRestoreSession { Sequence = 7, Refuse = { 17 } };

        var result = Restore(Snapshot(image, text), new ClipboardRestoreCondition(7), session);

        Assert.Equal(SnapshotRestoreResult.Failed, result);
        Assert.Equal(new[] { 13u }, session.Copies.Select(p => p.Format));
    }

    // ─── The restore is marked, as the transcript write is ──────────

    // The restore is itself a write, and clipboard history takes a write as a
    // new item: unmarked, a dictation could put a second copy of what the user
    // had copied into Win+V and offer it to cloud sync again. Whether Windows
    // honours the markers, or folds identical items together anyway, is a
    // device check (VERIFY.md).

    [Fact]
    public void A_restore_plan_puts_the_content_back_first_then_the_three_markers()
    {
        var text = TextFormat("before");
        var html = new ClipboardFormatData(0xC001, "HTML Format", [1, 2, 3]);

        var plan = ClipboardFormatSnapshot.RestorePlan(Snapshot(text, html));

        // The copied formats keep their numbers, bytes and listed order; the
        // markers have no number until the restore's session registers them,
        // and each is a DWORD of zero.
        Assert.Equal(
            new (uint?, string, string)[]
            {
                (13, "CF_UNICODETEXT", Convert.ToHexString(text.Data)),
                (0xC001, "HTML Format", "010203"),
                (null, "ExcludeClipboardContentFromMonitorProcessing", "00000000"),
                (null, "CanIncludeInClipboardHistory", "00000000"),
                (null, "CanUploadToCloudClipboard", "00000000"),
            },
            plan.Select(w => (w.Id, w.Name, Convert.ToHexString(w.Data))));
    }

    [Fact]
    public void The_restore_marks_with_the_same_names_and_payloads_as_the_transcript_write()
    {
        // Two lists in two projects that do not reference each other. A
        // marker renamed in one is a restore that history no longer skips.
        var restoreMarkers = ClipboardFormatSnapshot.RestorePlan(Snapshot(TextFormat("x"))).Skip(1);
        var writeMarkers = Win32Clipboard.Plan("x").Skip(1);

        Assert.Equal(
            writeMarkers.Select(w => (w.Name, Convert.ToHexString(w.Payload))),
            restoreMarkers.Select(w => ((string?)w.Name, Convert.ToHexString(w.Data))));
    }

    [Fact]
    public void The_users_own_markers_go_back_as_they_were_and_are_not_written_twice()
    {
        // A second write of the same format would replace the first, so the
        // copied content's own choice — here an explicit "yes, keep me in
        // history" — goes back untouched, and only the missing markers are
        // added. The names match whatever case the owner registered them in.
        var text = TextFormat("before");
        var optedIn = new ClipboardFormatData(0xC0B2, "canincludeinclipboardhistory", [1, 0, 0, 0]);

        var plan = ClipboardFormatSnapshot.RestorePlan(Snapshot(text, optedIn));

        Assert.Equal(
            new (uint?, string, string)[]
            {
                (13, "CF_UNICODETEXT", Convert.ToHexString(text.Data)),
                (0xC0B2, "canincludeinclipboardhistory", "01000000"),
                (null, "ExcludeClipboardContentFromMonitorProcessing", "00000000"),
                (null, "CanUploadToCloudClipboard", "00000000"),
            },
            plan.Select(w => (w.Id, w.Name, Convert.ToHexString(w.Data))));
    }

    [Fact]
    public void A_snapshot_carrying_all_three_markers_gets_none_added()
    {
        var formats = new[]
        {
            TextFormat("correct horse battery staple"),
            new ClipboardFormatData(0xC0B1, "ExcludeClipboardContentFromMonitorProcessing", [0, 0, 0, 0]),
            new ClipboardFormatData(0xC0B2, "CanIncludeInClipboardHistory", [0, 0, 0, 0]),
            new ClipboardFormatData(0xC0B3, "CanUploadToCloudClipboard", [0, 0, 0, 0]),
        };

        var plan = ClipboardFormatSnapshot.RestorePlan(Snapshot(formats));

        Assert.Equal(formats.Select(f => (uint?)f.Id), plan.Select(w => w.Id));
    }

    [Fact]
    public void An_empty_snapshot_gets_no_markers()
    {
        // Markers alone would leave something on a clipboard the user had
        // left empty.
        Assert.Empty(ClipboardFormatSnapshot.RestorePlan(ClipboardContents.Empty));
    }

    [Fact]
    public void A_restore_puts_its_markers_on_the_clipboard_not_just_in_its_plan()
    {
        var session = new RecordingRestoreSession { Sequence = 7 };

        var result = Restore(Snapshot(TextFormat("before")), new ClipboardRestoreCondition(7), session);

        Assert.Equal(SnapshotRestoreResult.Restored, result);
        Assert.Equal(
            new[] { "Sequence", "Empty", "Put", "Register", "Put", "Register", "Put", "Register", "Put" },
            session.Calls);
        Assert.Equal(
            new[]
            {
                ("ExcludeClipboardContentFromMonitorProcessing", "00000000"),
                ("CanIncludeInClipboardHistory", "00000000"),
                ("CanUploadToCloudClipboard", "00000000"),
            },
            session.Markers.Select(m => (m.Name, Convert.ToHexString(m.Data))));
    }

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("CanIncludeInClipboardHistory")]
    [InlineData("CanUploadToCloudClipboard")]
    public void A_marker_that_cannot_go_on_does_not_fail_the_restore_and_the_rest_still_do(string marker)
    {
        // The user's content is back, which is what the restore is for; and
        // each marker closes a different door, so losing one is no reason to
        // lose the others. Refused and unregistered are the two ways it fails.
        foreach (var session in new[]
                 {
                     new RecordingRestoreSession { Sequence = 7, RefuseNamed = { marker } },
                     new RecordingRestoreSession { Sequence = 7, Unregistered = { marker } },
                 })
        {
            var result = Restore(Snapshot(TextFormat("before")), new ClipboardRestoreCondition(7), session);

            Assert.Equal(SnapshotRestoreResult.Restored, result);
            Assert.Equal(new[] { 13u }, session.Copies.Select(c => c.Format));
            Assert.Equal(
                new[]
                {
                    "ExcludeClipboardContentFromMonitorProcessing",
                    "CanIncludeInClipboardHistory",
                    "CanUploadToCloudClipboard",
                }.Where(name => name != marker),
                session.Markers.Select(m => m.Name));
        }
    }

    private static SnapshotRestoreResult Restore(
        ClipboardContents contents, ClipboardRestoreCondition? condition, RecordingRestoreSession session) =>
        ClipboardFormatSnapshot.Restore(contents, condition, session, NullLogger.Instance);

    private static ClipboardContents Snapshot(params ClipboardFormatData[] formats) => new(formats);

    private static ClipboardFormatData TextFormat(string text) =>
        new(13, "CF_UNICODETEXT", Encoding.Unicode.GetBytes(text + '\0'));

    [Theory]
    [InlineData(13u, "CF_UNICODETEXT")]
    [InlineData(15u, "CF_HDROP")]
    [InlineData(17u, "CF_DIBV5")]
    [InlineData(2u, "CF_BITMAP")]
    public void Standard_formats_are_named_as_winuser_h_names_them(uint format, string name)
    {
        Assert.Equal(name, ClipboardFormatSnapshot.StandardName(format));
    }

    [Fact]
    public void A_registered_number_has_no_standard_name()
    {
        Assert.Null(ClipboardFormatSnapshot.StandardName(0xC001));
    }

    private static string Name(uint format) => ClipboardFormatSnapshot.StandardName(format) ?? $"#0x{format:X4}";

    private static byte[] Bytes(int count) => Enumerable.Range(0, count).Select(i => (byte)i).ToArray();

    /// <summary>
    /// A clipboard's reads, keyed by format. A null entry is a format whose
    /// owner returned nothing; a missing key is one this test never expects to
    /// be read. Records every format asked about and every one copied, because
    /// two of the claims — the unsafe formats are never read, and an oversized
    /// one is never copied — are about what did NOT happen.
    /// </summary>
    private sealed class FakeReader : IClipboardFormatReader, IEnumerable<KeyValuePair<uint, byte[]?>>
    {
        private readonly Dictionary<uint, byte[]?> _data = new();

        public List<uint> Asked { get; } = [];

        public List<uint> Copied { get; } = [];

        public byte[]? this[uint format]
        {
            set => _data[format] = value;
        }

        public long? SizeOf(uint format)
        {
            Asked.Add(format);
            return _data.TryGetValue(format, out var data) && data is not null ? data.Length : null;
        }

        public byte[]? Copy(uint format, long size)
        {
            Copied.Add(format);
            return _data.TryGetValue(format, out var data) ? data : null;
        }

        public IEnumerator<KeyValuePair<uint, byte[]?>> GetEnumerator() => _data.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// An open clipboard that records every call and tracks every block, so a
    /// test can say what ended up on the clipboard and that nothing was
    /// leaked or freed twice. Registered names get numbers from 0xC100 up, as
    /// a real session hands out numbers from 0xC000.
    /// </summary>
    private sealed class RecordingSession : IClipboardSession
    {
        private readonly Dictionary<string, uint> _numbers = new();
        private readonly Dictionary<IntPtr, byte[]> _blocks = new();
        private readonly List<IntPtr> _taken = [];
        private readonly List<IntPtr> _freed = [];
        private int _allocations;

        /// <summary>The ordinal of the allocation that fails, if any.</summary>
        public int? RefuseAllocation { get; init; }

        public bool EmptyFails { get; init; }

        /// <summary>Formats, by name, the clipboard refuses to take.</summary>
        public HashSet<string> Refuse { get; } = [];

        /// <summary>Names that cannot be registered.</summary>
        public HashSet<string> Unregistered { get; } = [];

        public List<Call> Calls { get; } = [];

        /// <summary>What the clipboard holds, in the order it was placed.</summary>
        public List<(string Name, byte[] Payload)> Placed { get; } = [];

        public int LastError => 5;

        public int Count(string op) => Calls.Count(c => c.Op == op);

        public IntPtr Allocate(byte[] payload)
        {
            Calls.Add(new Call("Allocate", Payload: payload));
            if (_allocations++ == RefuseAllocation)
            {
                return IntPtr.Zero;
            }

            var block = (IntPtr)(_blocks.Count + 1);
            _blocks[block] = payload;
            return block;
        }

        public bool Empty()
        {
            Calls.Add(new Call("Empty"));
            if (EmptyFails)
            {
                return false;
            }

            Placed.Clear();
            return true;
        }

        public uint Register(string name)
        {
            Calls.Add(new Call("Register", Name: name));
            if (Unregistered.Contains(name))
            {
                return 0;
            }

            if (!_numbers.TryGetValue(name, out var number))
            {
                number = 0xC100u + (uint)_numbers.Count;
                _numbers[name] = number;
            }

            return number;
        }

        public bool Set(uint format, IntPtr block)
        {
            var name = format == 13 ? "CF_UNICODETEXT" : _numbers.Single(n => n.Value == format).Key;
            Calls.Add(new Call("Set", Name: name));
            Assert.True(_blocks.ContainsKey(block), $"{name} was set from a block that was never allocated.");
            Assert.DoesNotContain(block, _freed);
            if (Refuse.Contains(name))
            {
                return false;
            }

            _taken.Add(block);
            Placed.Add((name, _blocks[block]));
            return true;
        }

        public void Free(IntPtr block)
        {
            Calls.Add(new Call("Free"));
            _freed.Add(block);
        }

        /// <summary>
        /// Every block allocated went exactly one way: to the clipboard, or
        /// back to the heap. Never both — that frees memory the system owns —
        /// and never neither, which leaks it.
        /// </summary>
        public void AssertEveryBlockTakenOrFreedOnce()
        {
            foreach (var block in _blocks.Keys)
            {
                var fates = _taken.Count(b => b == block) + _freed.Count(b => b == block);
                Assert.True(fates == 1, $"Block {block} was taken or freed {fates} times, not once.");
            }

            Assert.All(_freed, block => Assert.True(_blocks.ContainsKey(block), $"Freed {block}, which was never allocated."));
        }

        public sealed record Call(string Op, string? Name = null, byte[]? Payload = null);
    }

    /// <summary>
    /// A clipboard a restore holds open: records every call in order —
    /// reading the count included, because WHEN it is read is the claim — and
    /// what ended up on the clipboard.
    /// </summary>
    private sealed class RecordingRestoreSession : IClipboardRestoreSession
    {
        private uint _sequence;

        public uint Sequence
        {
            init => _sequence = value;
        }

        /// <summary>How many formats the clipboard holds when opened.</summary>
        public int Formats { get; init; }

        public bool EmptyFails { get; init; }

        private readonly Dictionary<string, uint> _registered = new();

        /// <summary>Formats, by number, the clipboard refuses to take.</summary>
        public HashSet<uint> Refuse { get; } = [];

        /// <summary>Registered formats, by name, the clipboard refuses to take.</summary>
        public HashSet<string> RefuseNamed { get; } = [];

        /// <summary>Names that cannot be registered.</summary>
        public HashSet<string> Unregistered { get; } = [];

        public List<string> Calls { get; } = [];

        /// <summary>Everything on the clipboard, in the order it was put there.</summary>
        public List<(uint Format, byte[] Data)> Placed { get; } = [];

        /// <summary>What was put back by the number it was copied under.</summary>
        public IEnumerable<(uint Format, byte[] Data)> Copies =>
            Placed.Where(p => !_registered.ContainsValue(p.Format));

        /// <summary>What was put on under a name this session registered.</summary>
        public IEnumerable<(string Name, byte[] Data)> Markers =>
            Placed.Where(p => _registered.ContainsValue(p.Format))
                .Select(p => (_registered.Single(r => r.Value == p.Format).Key, p.Data));

        public uint SequenceNumber
        {
            get
            {
                Calls.Add("Sequence");
                return _sequence;
            }
        }

        public int FormatCount
        {
            get
            {
                Calls.Add("FormatCount");
                return Formats;
            }
        }

        public int LastError => 5;

        public bool Empty()
        {
            Calls.Add("Empty");
            if (EmptyFails)
            {
                return false;
            }

            Placed.Clear();
            return true;
        }

        // Numbered from 0xC100 up, as a real session hands out numbers from
        // 0xC000; none of the copied formats in these tests uses that range.
        public uint Register(string name)
        {
            Calls.Add("Register");
            if (Unregistered.Contains(name))
            {
                return 0;
            }

            if (!_registered.TryGetValue(name, out var number))
            {
                number = 0xC100u + (uint)_registered.Count;
                _registered[name] = number;
            }

            return number;
        }

        public bool Put(uint format, byte[] data)
        {
            Calls.Add("Put");
            if (Refuse.Contains(format) || RefuseNamed.Any(name => _registered.GetValueOrDefault(name) == format))
            {
                return false;
            }

            Placed.Add((format, data));
            return true;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static Win32ReadableClipboard Clipboard(IClipboard writer) =>
        new(writer, NullLogger<Win32ReadableClipboard>.Instance);

    /// <summary>
    /// Records every write rather than only the last one, because two of the
    /// claims here — that the text arrives unchanged, and that a refusal is
    /// not retried — are claims about the sequence of calls and are both
    /// invisible to a fake that keeps a single "last value" field.
    /// </summary>
    /// <remarks>
    /// Implements <see cref="IClipboard"/> only, deliberately: this stands in
    /// for the <c>Win32Clipboard</c> that <c>Win32ReadableClipboard</c>
    /// actually composes over in the app, which is write-only for the reason
    /// the wrapper exists at all.
    /// </remarks>
    private sealed class InMemoryClipboard : IClipboard
    {
        public bool Succeeds { get; init; } = true;

        public List<string> Writes { get; } = [];

        public int Calls => Writes.Count;

        public bool TrySet(string text)
        {
            // Recorded before the verdict, so a refused write is still visible
            // as an attempt. A fake that only recorded successes could not
            // tell "tried once and was refused" from "retried and was refused
            // twice".
            Writes.Add(text);
            return Succeeds;
        }
    }
}
