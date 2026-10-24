using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Services.Tests;

/// <summary>
/// dictation "scratch that" — what gets remembered, and what gets deleted.
///
/// <para>
/// The whole feature is one keystroke away from its own worst failure:
/// backspaces are destructive and untargeted, so the only thing standing between a user and a run of deletes through text they
/// wrote by hand is that the record is written on exactly one branch and spent
/// on exactly one use. Both halves are pinned here.
/// </para>
///
/// <para>
/// The gate itself — two minutes, one window, a password field — belongs to
/// <c>DictationUndoPolicyTests</c>, which can stage all three without a
/// desktop. What this file asserts is the WIRING: that a refusal reaches the
/// keyboard as zero keystrokes, that an allowed decision reaches it as exactly
/// its own count, and that neither can happen twice off one insertion.
/// </para>
/// </summary>
[Collection(LastInsertionCollection.Name)]
public class FocusedTextInsertionUndoTests : IDisposable
{
    public FocusedTextInsertionUndoTests() => LastInsertionRegistry.Clear();

    public void Dispose() => LastInsertionRegistry.Clear();

    // ─── The desktop, faked ─────────────────────────────────────────

    /// <summary>A resolver that also answers "which window has keyboard
    /// focus", handing back queued readings and repeating the last forever.
    /// The same shape <c>FocusedTextInsertionFocusTests</c> uses.</summary>
    private sealed class SeeingResolver : IFocusedApplicationResolver, IKeyboardFocusProbe
    {
        private readonly Queue<KeyboardFocusTarget> _readings;
        private KeyboardFocusTarget _last;

        public SeeingResolver(params KeyboardFocusTarget[] readings)
        {
            _readings = new Queue<KeyboardFocusTarget>(readings);
            _last = readings.Length > 0 ? readings[^1] : KeyboardFocusTarget.Unknown;
        }

        public FocusedApplicationResult Resolve() =>
            FocusedApplicationResult.Resolved("notepad", 4242, "Untitled - Notepad");

        public KeyboardFocusTarget Read() =>
            _readings.Count > 0 ? _readings.Dequeue() : _last;
    }

    /// <summary>A resolver with no focus probe at all — every host that has
    /// not wired one.</summary>
    private sealed class BlindResolver : IFocusedApplicationResolver
    {
        public FocusedApplicationResult Resolve() =>
            FocusedApplicationResult.Resolved("notepad", 4242, "Untitled - Notepad");
    }

    private sealed class Clipboard : IClipboard, IClipboardTextSnapshot
    {
        public string? Held { get; set; } = "the user's own clipboard";

        public bool Readable { get; set; } = true;

        public bool Writable { get; set; } = true;

        public bool TrySet(string text)
        {
            if (!Writable)
            {
                return false;
            }

            Held = text;
            return true;
        }

        public bool TryRead(out string? text)
        {
            text = Held;
            return Readable;
        }
    }

    private sealed class Paste : IPasteKeystroke
    {
        public bool Succeeds { get; set; } = true;

        public bool Throws { get; set; }

        public bool TrySend() =>
            Throws ? throw new InvalidOperationException("the keyboard blew up") : Succeeds;
    }

    private sealed class StubSecureFieldProbe : ISecureFieldProbe
    {
        public SecureFieldState State { get; set; } = SecureFieldState.NotSecure;

        public bool Throws { get; set; }

        public SecureFieldState Probe() =>
            Throws ? throw new InvalidOperationException("UIA declined") : State;
    }

    /// <summary>
    /// The keyboard, counted. Every assertion about "no keystroke was
    /// synthesised" is an assertion about this object — a test that only
    /// checked the returned refusal would pass just as happily against an
    /// implementation that refused AFTER pressing the keys.
    /// </summary>
    private sealed class Backspace : IBackspaceKeystroke
    {
        public List<int> Requests { get; } = new();

        public int Delivers { get; set; } = -1;

        public bool Throws { get; set; }

        public Task<int> SendAsync(int count, CancellationToken ct = default)
        {
            Requests.Add(count);

            if (Throws)
            {
                throw new InvalidOperationException("SendInput blew up");
            }

            return Task.FromResult(Delivers < 0 ? count : Delivers);
        }
    }

    private static KeyboardFocusTarget TextSurface =>
        KeyboardFocusTarget.Of(0x505b8, "RichEditD2DPT");

    private static KeyboardFocusTarget MenuBarIsland =>
        KeyboardFocusTarget.Of(0x505a6, "InputSiteWindowClass");

    private static readonly DateTimeOffset Noon =
        new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static FocusedTextInsertion Insertion(
        IFocusedApplicationResolver resolver,
        IClipboard clipboard,
        IPasteKeystroke paste,
        IBackspaceKeystroke? backspace = null,
        ISecureFieldProbe? secureField = null,
        Func<DateTimeOffset>? utcNow = null,
        ILogger<FocusedTextInsertion>? logger = null) =>
        new(
            resolver,
            clipboard,
            paste,
            logger ?? NullLogger<FocusedTextInsertion>.Instance,
            pasteSettleDelay: TimeSpan.Zero,
            backspace: backspace,
            secureField: secureField ?? new StubSecureFieldProbe(),
            utcNow: utcNow ?? (() => Noon));

    /// <summary>The ordinary case: one window that holds still, a probe that
    /// says the field is not a password box, and a clock that does not
    /// move.</summary>
    private static FocusedTextInsertion Steady(Backspace backspace) =>
        Insertion(
            new SeeingResolver(TextSurface),
            new Clipboard(),
            new Paste(),
            backspace);

    // ─── What gets recorded, and what must not ──────────────────────

    [Fact]
    public async Task A_confirmed_insertion_records_the_text_the_window_and_the_time()
    {
        await Steady(new Backspace()).InsertAsync("the spoken sentence");

        var record = LastInsertionRegistry.Peek();

        Assert.NotNull(record);
        // The text EXACTLY as inserted. The undo counts keystrokes off this
        // string, so a record that trimmed or reformatted it would be deciding
        // how much of the user's document gets deleted.
        Assert.Equal("the spoken sentence", record!.Text);
        // The window the keystroke actually went to, read at send time — which
        // is the window the policy has to see again before it presses
        // backspace.
        Assert.Equal((nint)0x505b8, record.WindowHandle);
        Assert.Equal("RichEditD2DPT", record.WindowClass);
        Assert.Equal(Noon, record.AtUtc);
    }

    [Fact]
    public async Task A_clipboard_that_would_not_take_the_text_records_nothing()
    {
        // No keystroke was sent, so nothing was typed, so there is nothing to
        // take back. A record here would arm backspaces over text the user
        // wrote themselves.
        var result = await Insertion(
            new SeeingResolver(TextSurface),
            new Clipboard { Writable = false },
            new Paste(),
            new Backspace())
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        Assert.Null(LastInsertionRegistry.Peek());
    }

    [Fact]
    public async Task An_empty_transcript_records_nothing()
    {
        var result = await Steady(new Backspace()).InsertAsync("   ");

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        Assert.Null(LastInsertionRegistry.Peek());
    }

    [Fact]
    public async Task A_focus_move_records_nothing()
    {
        // The transcript reached the clipboard and no further. Recording here
        // would let "scratch that" delete from the window the user moved TO.
        var result = await Insertion(
            new SeeingResolver(TextSurface, MenuBarIsland),
            new Clipboard(),
            new Paste(),
            new Backspace())
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.FocusMoved, result.Outcome);
        Assert.Null(LastInsertionRegistry.Peek());
    }

    [Fact]
    public async Task A_paste_that_reported_failure_records_nothing()
    {
        var result = await Insertion(
            new SeeingResolver(TextSurface),
            new Clipboard(),
            new Paste { Succeeds = false },
            new Backspace())
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        Assert.Null(LastInsertionRegistry.Peek());
    }

    [Fact]
    public async Task A_paste_that_threw_records_nothing()
    {
        // "Refused means no evidence it was delivered, not proof it was not" —
        // and on that reading the record must NOT be written. A throw after the
        // keystroke went would make the undo a delete over text; a throw before
        // it went would make the undo a delete over the user's own words. The
        // reversible half of that choice is to remember nothing.
        var result = await Insertion(
            new SeeingResolver(TextSurface),
            new Clipboard(),
            new Paste { Throws = true },
            new Backspace())
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.InsertionRefused, result.Outcome);
        Assert.Null(LastInsertionRegistry.Peek());
    }

    [Fact]
    public async Task An_insertion_whose_clipboard_could_not_be_restored_is_still_recorded()
    {
        // ClipboardRestoreFailed typed the text — TextWasDelivered is true —
        // so the user's document changed and "scratch that" has to work. A
        // user who has just lost their clipboard is the last person who should
        // also be told there is nothing to undo.
        var result = await Insertion(
            new SeeingResolver(TextSurface),
            new Clipboard { Readable = false },
            new Paste(),
            new Backspace())
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.ClipboardRestoreFailed, result.Outcome);
        Assert.True(result.TextWasDelivered);
        Assert.Equal("the spoken sentence", LastInsertionRegistry.Peek()?.Text);
    }

    [Fact]
    public async Task An_insertion_with_no_focus_probe_records_a_window_nothing_can_match()
    {
        // A host with no probe can insert and cannot undo, and that is the safe
        // direction: handle 0 and a null class are what DictationUndoPolicy
        // reads as "we could not tell which window this is", which it refuses
        // rather than passes.
        var backspace = new Backspace();
        var inserter = Insertion(
            new BlindResolver(), new Clipboard(), new Paste(), backspace);

        await inserter.InsertAsync("the spoken sentence");

        Assert.Equal((nint)0, LastInsertionRegistry.Peek()?.WindowHandle);
        Assert.Null(LastInsertionRegistry.Peek()?.WindowClass);

        var undo = await inserter.UndoLastInsertionAsync();

        Assert.Equal(DictationUndoRefusal.DifferentWindow, undo.Refusal);
        Assert.Empty(backspace.Requests);
    }

    // ─── The delete ─────────────────────────────────────────────────

    [Fact]
    public async Task The_count_sent_is_the_count_the_policy_decided()
    {
        var backspace = new Backspace();
        var inserter = Steady(backspace);

        await inserter.InsertAsync("the spoken sentence");
        var undo = await inserter.UndoLastInsertionAsync();

        Assert.True(undo.Undone);
        Assert.Equal(DictationUndoRefusal.None, undo.Refusal);
        Assert.Equal(new[] { 19 }, backspace.Requests);
        Assert.Equal(19, undo.BackspaceCount);
    }

    [Fact]
    public async Task The_count_is_grapheme_clusters_and_not_code_units()
    {
        // Two emoji are four UTF-16 code units and two backspaces. A count in
        // code units overruns by two characters into text the user wrote by
        // hand, in somebody else's document — which is why the count comes
        // from DictationDeleteCommand and is never recomputed here.
        var backspace = new Backspace();
        var inserter = Steady(backspace);

        await inserter.InsertAsync("🙂🙂");
        await inserter.UndoLastInsertionAsync();

        Assert.Equal(new[] { 2 }, backspace.Requests);
    }

    [Fact]
    public async Task The_record_is_spent_by_one_undo()
    {
        // THE GUARD AGAINST WALKING BACKWARDS. Without it a user who says
        // "scratch that" twice deletes the sentence they dictated and then the
        // sentence before it, which nothing has age-checked, window-checked or
        // counted.
        var backspace = new Backspace();
        var inserter = Steady(backspace);

        await inserter.InsertAsync("the spoken sentence");
        await inserter.UndoLastInsertionAsync();

        var second = await inserter.UndoLastInsertionAsync();

        Assert.Equal(DictationUndoRefusal.NothingRecorded, second.Refusal);
        Assert.False(second.Undone);
        Assert.Equal(new[] { 19 }, backspace.Requests);
        Assert.Null(LastInsertionRegistry.Peek());
    }

    [Fact]
    public async Task An_undo_with_nothing_recorded_sends_no_keystroke()
    {
        var backspace = new Backspace();

        var undo = await Steady(backspace).UndoLastInsertionAsync();

        Assert.Equal(DictationUndoRefusal.NothingRecorded, undo.Refusal);
        Assert.Empty(backspace.Requests);
    }

    // ─── The refusals reach the keyboard as silence ─────────────────

    [Fact]
    public async Task A_password_field_sends_no_keystroke()
    {
        // Pressing backspace in a field is no less a write than typing into
        // it, and this is the branch macOS shipped missing for one review
        // cycle: dictate into a normal field, click a password field in the
        // same app, say "scratch that".
        var backspace = new Backspace();
        var probe = new StubSecureFieldProbe();
        var inserter = Insertion(
            new SeeingResolver(TextSurface),
            new Clipboard(),
            new Paste(),
            backspace,
            probe);

        await inserter.InsertAsync("the spoken sentence");
        probe.State = SecureFieldState.Secure;

        var undo = await inserter.UndoLastInsertionAsync();

        Assert.Equal(DictationUndoRefusal.SecureField, undo.Refusal);
        Assert.False(undo.Undone);
        Assert.Equal(0, undo.BackspaceCount);
        Assert.Empty(backspace.Requests);
    }

    [Fact]
    public async Task A_probe_that_could_not_answer_sends_no_keystroke_either()
    {
        // The OPPOSITE direction from the insert path, on purpose. Unknown is
        // "carry on" when the question is whether to type, because refusing
        // there switches dictation off for people nowhere near a password box.
        // It is "stop" when the question is whether to delete: the cost of
        // being wrong is four words retyped one way and an invisible write into
        // a credential field the other.
        var backspace = new Backspace();
        var probe = new StubSecureFieldProbe();
        var inserter = Insertion(
            new SeeingResolver(TextSurface), new Clipboard(), new Paste(), backspace, probe);

        await inserter.InsertAsync("the spoken sentence");
        probe.State = SecureFieldState.Unknown;

        var undo = await inserter.UndoLastInsertionAsync();

        Assert.Equal(DictationUndoRefusal.SecureField, undo.Refusal);
        Assert.Empty(backspace.Requests);
    }

    [Fact]
    public async Task A_probe_that_threw_sends_no_keystroke_either()
    {
        var backspace = new Backspace();
        var probe = new StubSecureFieldProbe();
        var inserter = Insertion(
            new SeeingResolver(TextSurface), new Clipboard(), new Paste(), backspace, probe);

        await inserter.InsertAsync("the spoken sentence");
        probe.Throws = true;

        var undo = await inserter.UndoLastInsertionAsync();

        // The guard failing loudly must not be a way past it.
        Assert.Equal(DictationUndoRefusal.SecureField, undo.Refusal);
        Assert.Empty(backspace.Requests);
    }

    [Fact]
    public async Task A_build_with_no_secure_field_probe_never_undoes()
    {
        // Deliberate pressure on the composition root: forgetting the probe
        // loses "scratch that", which somebody notices, instead of gaining a
        // silent delete into password boxes, which nobody does.
        var backspace = new Backspace();
        var inserter = new FocusedTextInsertion(
            new SeeingResolver(TextSurface),
            new Clipboard(),
            new Paste(),
            NullLogger<FocusedTextInsertion>.Instance,
            pasteSettleDelay: TimeSpan.Zero,
            backspace: backspace,
            secureField: null,
            utcNow: () => Noon);

        await inserter.InsertAsync("the spoken sentence");
        var undo = await inserter.UndoLastInsertionAsync();

        Assert.Equal(DictationUndoRefusal.SecureField, undo.Refusal);
        Assert.Empty(backspace.Requests);
    }

    [Fact]
    public async Task A_stale_record_sends_no_keystroke()
    {
        var backspace = new Backspace();
        var clock = Noon;
        var inserter = Insertion(
            new SeeingResolver(TextSurface),
            new Clipboard(),
            new Paste(),
            backspace,
            utcNow: () => clock);

        await inserter.InsertAsync("the spoken sentence");
        clock = Noon + DictationUndoPolicy.MaxAge + TimeSpan.FromSeconds(1);

        var undo = await inserter.UndoLastInsertionAsync();

        Assert.Equal(DictationUndoRefusal.Expired, undo.Refusal);
        Assert.Empty(backspace.Requests);
    }

    [Fact]
    public async Task A_window_the_user_has_moved_away_from_sends_no_keystroke()
    {
        // Two readings for the insert (aim, fire) and a third for the undo.
        var backspace = new Backspace();
        var inserter = Insertion(
            new SeeingResolver(TextSurface, TextSurface, MenuBarIsland),
            new Clipboard(),
            new Paste(),
            backspace);

        await inserter.InsertAsync("the spoken sentence");
        var undo = await inserter.UndoLastInsertionAsync();

        Assert.Equal(DictationUndoRefusal.DifferentWindow, undo.Refusal);
        Assert.Empty(backspace.Requests);
    }

    // ─── When the keyboard itself will not cooperate ────────────────

    [Fact]
    public async Task A_build_with_no_backspace_seam_says_so_rather_than_refusing_quietly()
    {
        var inserter = Insertion(
            new SeeingResolver(TextSurface), new Clipboard(), new Paste(), backspace: null);

        await inserter.InsertAsync("the spoken sentence");
        var undo = await inserter.UndoLastInsertionAsync();

        // Not a DictationUndoRefusal: the policy allowed this one, and telling
        // the user "there was nothing to undo" would be a lie about their own
        // sentence.
        Assert.Equal(DictationUndoRefusal.None, undo.Refusal);
        Assert.False(undo.Undone);
        Assert.NotEmpty(undo.Reason);
    }

    [Fact]
    public async Task A_backspace_seam_that_threw_does_not_throw_out()
    {
        var backspace = new Backspace { Throws = true };
        var inserter = Steady(backspace);

        await inserter.InsertAsync("the spoken sentence");
        var undo = await inserter.UndoLastInsertionAsync();

        Assert.False(undo.Undone);
        Assert.Equal(DictationUndoRefusal.None, undo.Refusal);
        Assert.Contains("SendInput blew up", undo.Reason);
    }

    [Fact]
    public async Task A_run_that_stopped_half_way_is_not_reported_as_a_clean_undo()
    {
        // Part of the sentence is still on screen and the record is gone, so
        // there is no second attempt. A result that claimed 19 would hide
        // exactly that.
        var backspace = new Backspace { Delivers = 8 };
        var inserter = Steady(backspace);

        await inserter.InsertAsync("the spoken sentence");
        var undo = await inserter.UndoLastInsertionAsync();

        Assert.True(undo.Undone);
        Assert.Equal(8, undo.BackspaceCount);
        Assert.Contains("8 of 19", undo.Reason);
    }

    // ─── The value type ─────────────────────────────────────────────

    [Fact]
    public void A_defaulted_result_claims_nothing_was_deleted()
    {
        // DictationUndoRefusal.None is the permissive member of its enum, so a
        // defaulted result reads as "allowed" on that field alone. It is safe
        // only because the two fields are read together, and this is what says
        // so out loud.
        var defaulted = default(FocusedUndoResult);

        Assert.False(defaulted.Undone);
        Assert.Equal(0, defaulted.BackspaceCount);
        Assert.Equal(string.Empty, defaulted.Reason);
    }

    [Fact]
    public void The_inserter_is_the_thing_that_can_be_asked_to_undo()
    {
        // The seam is discovered at run time by whoever holds an
        // IFocusedTextInserter, so nothing in the type system says the shipped
        // inserter implements it — and an undo nobody implements is a spoken
        // command that silently does nothing.
        Assert.True(typeof(IFocusedTextUndo).IsAssignableFrom(typeof(FocusedTextInsertion)));
    }
}
