using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// dictation insert — the paste-settle contract.
///
/// <para>
/// The clipboard-borrowing scheme is only correct if the borrowed transcript
/// is still ON the clipboard when the target application processes the
/// synthesised Ctrl+V — which happens on the target's message loop, after
/// <c>IPasteKeystroke.TrySend</c> has already returned. The shipped defect
/// these tests pin against: the restore ran immediately in the
/// <c>finally</c>, the target lost the race (Windows Terminal reliably), and
/// the user's OLD clipboard was pasted over their spoken sentence while every
/// log line reported success.
/// </para>
/// </summary>
public class FocusedTextInsertionSettleTests
{
    private sealed class Resolver : IFocusedApplicationResolver
    {
        public FocusedApplicationResult Resolve() =>
            FocusedApplicationResult.Resolved("notepad", 4242, "Untitled - Notepad");
    }

    /// <summary>Records the wall-clock of every write, so a test can prove
    /// the restore write happened a full settle-delay after the paste.</summary>
    private sealed class TimestampingClipboard : IClipboard, IClipboardTextSnapshot
    {
        public string? Held { get; set; } = "the user's own clipboard";
        public List<(string Text, long Ticks)> Writes { get; } = new();

        public bool TrySet(string text)
        {
            Writes.Add((text, System.Diagnostics.Stopwatch.GetTimestamp()));
            Held = text;
            return true;
        }

        public bool TryRead(out string? text)
        {
            text = Held;
            return true;
        }
    }

    private sealed class TimestampingPaste : IPasteKeystroke
    {
        public bool Succeeds { get; set; } = true;
        public long? SentAtTicks { get; private set; }
        public string? ClipboardWhenSent { get; private set; }

        private readonly TimestampingClipboard _clipboard;
        public TimestampingPaste(TimestampingClipboard clipboard) => _clipboard = clipboard;

        public bool TrySend()
        {
            SentAtTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            // What a real target would read — captured at send time, because
            // that is the only moment the fake can stand in for a message
            // loop that reads "later".
            ClipboardWhenSent = _clipboard.Held;
            return Succeeds;
        }
    }

    [Fact]
    public async Task TheTranscriptIsOnTheClipboardWhenThePasteGoes()
    {
        var clipboard = new TimestampingClipboard();
        var paste = new TimestampingPaste(clipboard);
        var inserter = new FocusedTextInsertion(
            new Resolver(), clipboard, paste,
            NullLogger<FocusedTextInsertion>.Instance,
            pasteSettleDelay: TimeSpan.Zero);

        var result = await inserter.InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal("the spoken sentence", paste.ClipboardWhenSent);
        // And the user's clipboard went back afterwards.
        Assert.Equal("the user's own clipboard", clipboard.Held);
    }

    [Fact]
    public async Task TheRestoreWaitsTheSettleDelayAfterADeliveredPaste()
    {
        // A real (small) delay, because the ordering claim is temporal: the
        // restore write must land no sooner than the settle delay after the
        // keystroke went. 120 ms asserted at >=90 ms keeps it honest without
        // making the suite timing-fragile.
        var settle = TimeSpan.FromMilliseconds(120);
        var clipboard = new TimestampingClipboard();
        var paste = new TimestampingPaste(clipboard);
        var inserter = new FocusedTextInsertion(
            new Resolver(), clipboard, paste,
            NullLogger<FocusedTextInsertion>.Instance,
            pasteSettleDelay: settle);

        var result = await inserter.InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.NotNull(paste.SentAtTicks);

        // Writes[0] is the transcript, Writes[1] is the restore.
        Assert.Equal(2, clipboard.Writes.Count);
        var restoreDelay = TimeSpan.FromSeconds(
            (clipboard.Writes[1].Ticks - paste.SentAtTicks!.Value)
            / (double)System.Diagnostics.Stopwatch.Frequency);
        Assert.True(
            restoreDelay >= TimeSpan.FromMilliseconds(90),
            $"the clipboard was restored {restoreDelay.TotalMilliseconds:F0} ms "
            + "after the paste — inside the window where the target reads it, "
            + "which is the shipped paste-your-old-clipboard defect");
    }

    [Fact]
    public async Task AnUndeliveredPasteRestoresImmediately()
    {
        // No keystroke went, so there is no reader to wait for — a failed
        // send must not cost the user half a second of held clipboard.
        var settle = TimeSpan.FromSeconds(30); // would time the test out if waited
        var clipboard = new TimestampingClipboard();
        var paste = new TimestampingPaste(clipboard) { Succeeds = false };
        var inserter = new FocusedTextInsertion(
            new Resolver(), clipboard, paste,
            NullLogger<FocusedTextInsertion>.Instance,
            pasteSettleDelay: settle);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await inserter.InsertAsync("the spoken sentence");
        sw.Stop();

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal("the user's own clipboard", clipboard.Held);
    }

    // ─── The whole-clipboard restore keeps the same contract ────────

    /// <summary>
    /// A clipboard that restores every format and counts changes, as the
    /// Windows one does. The lock models its atomic compare and restore.
    /// </summary>
    private sealed class CountingClipboard : IClipboard, IClipboardSnapshot
    {
        private readonly object _gate = new();
        private ClipboardFormatData[] _held = [Text("the user's own clipboard")];
        private uint _sequence = 7;

        public List<long> RestoreTicks { get; } = new();
        public Action? BeforeRestore { get; set; }

        public string? HeldText
        {
            get { lock (_gate) { return TextOf(_held); } }
        }

        public bool TrySet(string text)
        {
            lock (_gate) { _held = [Text(text)]; _sequence++; }
            return true;
        }

        public ClipboardContents? Capture()
        {
            lock (_gate) { return new ClipboardContents(_held); }
        }

        public SnapshotRestoreResult Restore(ClipboardContents contents, ClipboardRestoreCondition? condition)
        {
            BeforeRestore?.Invoke();
            // The lock stands for the open clipboard: the condition is
            // checked where no other writer can get in, as Win32 does it.
            lock (_gate)
            {
                if (condition is { } expected
                    && (_sequence != expected.Sequence || (expected.HoldsNothing && _held.Length > 0)))
                {
                    return SnapshotRestoreResult.Changed;
                }

                RestoreTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp());
                _held = [.. contents.Formats];
                _sequence++;
            }

            return SnapshotRestoreResult.Restored;
        }

        public uint SequenceNumber
        {
            get { lock (_gate) { return _sequence; } }
        }

        public void SomebodyElseCopies(string text) => TrySet(text);

        private static ClipboardFormatData Text(string text) =>
            new(13, "CF_UNICODETEXT", System.Text.Encoding.Unicode.GetBytes(text));

        private static string? TextOf(ClipboardFormatData[] held) =>
            held.Length == 0 ? null : System.Text.Encoding.Unicode.GetString(held[0].Data);
    }

    private sealed class CountingPaste : IPasteKeystroke
    {
        private readonly CountingClipboard _clipboard;
        public CountingPaste(CountingClipboard clipboard) => _clipboard = clipboard;

        public long? SentAtTicks { get; private set; }
        public string? ClipboardWhenSent { get; private set; }

        public bool TrySend()
        {
            SentAtTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            ClipboardWhenSent = _clipboard.HeldText;
            return true;
        }
    }

    [Fact]
    public async Task TheWholeClipboardRestoreAlsoWaitsTheSettleDelay()
    {
        // The same race as above, on the new path: restoring every format
        // must not land inside the window where the target is still reading
        // the transcript.
        var settle = TimeSpan.FromMilliseconds(120);
        var clipboard = new CountingClipboard();
        var paste = new CountingPaste(clipboard);
        var inserter = new FocusedTextInsertion(
            new Resolver(), clipboard, paste,
            NullLogger<FocusedTextInsertion>.Instance,
            pasteSettleDelay: settle);

        var result = await inserter.InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal("the spoken sentence", paste.ClipboardWhenSent);
        Assert.Equal("the user's own clipboard", clipboard.HeldText);
        var restoreDelay = TimeSpan.FromSeconds(
            (Assert.Single(clipboard.RestoreTicks) - paste.SentAtTicks!.Value)
            / (double)System.Diagnostics.Stopwatch.Frequency);
        Assert.True(
            restoreDelay >= TimeSpan.FromMilliseconds(90),
            $"the clipboard was restored {restoreDelay.TotalMilliseconds:F0} ms after the paste");
    }

    [Fact]
    public async Task ACopyImmediatelyBeforeThePostSettleRestoreIsNotOverwritten()
    {
        // Copy at the atomic restore boundary, after the settle has elapsed.
        // A separately scheduled writer can run after the restore on a busy
        // machine, so it cannot establish this ordering. Snapshot-restore
        // tests separately keep the sequence captured before the paste.
        var settle = TimeSpan.FromMilliseconds(120);
        var clipboard = new CountingClipboard();
        var paste = new CountingPaste(clipboard);
        long? copiedAtTicks = null;
        clipboard.BeforeRestore = () =>
        {
            copiedAtTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            clipboard.SomebodyElseCopies("copied before the restore");
        };
        var inserter = new FocusedTextInsertion(
            new Resolver(), clipboard, paste,
            NullLogger<FocusedTextInsertion>.Instance,
            pasteSettleDelay: settle);

        var result = await inserter.InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal("the spoken sentence", paste.ClipboardWhenSent);
        Assert.NotNull(copiedAtTicks);
        Assert.Contains("changed", result.Reason);
        var copyDelay = TimeSpan.FromSeconds(
            (copiedAtTicks!.Value - paste.SentAtTicks!.Value)
            / (double)System.Diagnostics.Stopwatch.Frequency);
        Assert.True(copyDelay >= TimeSpan.FromMilliseconds(90),
            $"the restore began {copyDelay.TotalMilliseconds:F0} ms after the paste");
        Assert.Empty(clipboard.RestoreTicks);
        Assert.Equal("copied before the restore", clipboard.HeldText);
    }
}
