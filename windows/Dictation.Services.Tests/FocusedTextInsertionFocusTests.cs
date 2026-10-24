using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// dictation insert — which WINDOW the keystroke was aimed at.
///
/// <para>
/// The defect these pin against: Windows 11 Notepad received a dictated
/// sentence and showed an empty document, while the log said
/// <c>Inserted 358 characters into Notepad</c>. The keystroke reached the
/// right PROCESS — <c>GetForegroundWindow</c> answered Notepad throughout —
/// and the wrong WINDOW inside it, because a lone Alt had armed the menu bar
/// and keyboard focus had moved from <c>RichEditD2DPT</c> to
/// <c>InputSiteWindowClass</c>. The root cause is fixed in
/// <see cref="HoldKeyState"/>; what is fixed here is that the inserter could
/// not see the difference and said so with total confidence anyway.
/// </para>
///
/// <para>
/// Two claims are tested. The paste is refused when the focus target moves
/// between aiming and firing, and the focus target is named in the line that
/// reports a delivery — so the next time a paste vanishes, the log says which
/// window ate it.
/// </para>
/// </summary>
public class FocusedTextInsertionFocusTests
{
    /// <summary>A resolver that cannot be asked about focus at all — every
    /// host that has not wired a probe, and the shape this must not break.</summary>
    private sealed class BlindResolver : IFocusedApplicationResolver
    {
        public FocusedApplicationResult Resolve() =>
            FocusedApplicationResult.Resolved("notepad", 4242, "Untitled - Notepad");
    }

    /// <summary>
    /// A resolver that also answers "which window has keyboard focus", handing
    /// back the queued readings in order and repeating the last one forever.
    /// </summary>
    private sealed class SeeingResolver : IFocusedApplicationResolver, IKeyboardFocusProbe
    {
        private readonly Queue<KeyboardFocusTarget> _readings;
        private KeyboardFocusTarget _last;

        public SeeingResolver(params KeyboardFocusTarget[] readings)
        {
            _readings = new Queue<KeyboardFocusTarget>(readings);
            _last = readings.Length > 0 ? readings[^1] : KeyboardFocusTarget.Unknown;
        }

        public bool Throws { get; set; }

        public int Reads { get; private set; }

        public FocusedApplicationResult Resolve() =>
            FocusedApplicationResult.Resolved("notepad", 4242, "Untitled - Notepad");

        public KeyboardFocusTarget Read()
        {
            Reads++;
            if (Throws)
            {
                throw new InvalidOperationException("the probe blew up");
            }

            return _readings.Count > 0 ? _readings.Dequeue() : _last;
        }
    }

    private sealed class Clipboard : IClipboard, IClipboardTextSnapshot
    {
        public string? Held { get; set; } = "the user's own clipboard";
        public List<string> Writes { get; } = new();

        public bool TrySet(string text)
        {
            Writes.Add(text);
            Held = text;
            return true;
        }

        public bool TryRead(out string? text)
        {
            text = Held;
            return true;
        }
    }

    private sealed class CountingPaste : IPasteKeystroke
    {
        public int Sends { get; private set; }

        public bool TrySend()
        {
            Sends++;
            return true;
        }
    }

    /// <summary>Keeps every formatted line, so a test can assert on what a
    /// human reading the log at 11:13 would actually have seen.</summary>
    private sealed class RecordingLogger : ILogger<FocusedTextInsertion>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private static FocusedTextInsertion Insertion(
        IFocusedApplicationResolver resolver,
        IClipboard clipboard,
        IPasteKeystroke paste,
        ILogger<FocusedTextInsertion>? logger = null) =>
        new(
            resolver,
            clipboard,
            paste,
            logger ?? NullLogger<FocusedTextInsertion>.Instance,
            pasteSettleDelay: TimeSpan.Zero);

    private static KeyboardFocusTarget TextSurface =>
        KeyboardFocusTarget.Of(0x505b8, "RichEditD2DPT");

    private static KeyboardFocusTarget MenuBarIsland =>
        KeyboardFocusTarget.Of(0x505a6, "InputSiteWindowClass");

    /// <summary>
    /// Windows Terminal's focused window. It has no caret and no document,
    /// and it takes a paste perfectly — see
    /// <see cref="A_target_with_no_caret_of_its_own_is_still_typed_into"/>.
    /// </summary>
    private static KeyboardFocusTarget CaretlessButWorking =>
        KeyboardFocusTarget.Of(0x204f2, "Windows.UI.Input.InputSite.WindowClass");

    // ─── The refusal ────────────────────────────────────────────────

    [Fact]
    public async Task A_focus_target_that_moves_between_aiming_and_typing_is_not_typed_into()
    {
        // SendInput injects at the keyboard, not at a window. If focus has
        // moved since the target was resolved, the sentence lands somewhere
        // nobody authorised — which is how a dictation ends up in a password
        // box.
        var clipboard = new Clipboard();
        var paste = new CountingPaste();
        var result = await Insertion(
            new SeeingResolver(TextSurface, MenuBarIsland), clipboard, paste)
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.FocusMoved, result.Outcome);
        Assert.Equal(0, paste.Sends);
        Assert.False(result.TextWasDelivered);
    }

    [Fact]
    public async Task A_refusal_for_a_moved_focus_still_puts_the_users_clipboard_back()
    {
        // The transcript is already on the clipboard by the time the second
        // reading happens. Bailing out is not a licence to leave it there.
        var clipboard = new Clipboard();
        var result = await Insertion(
            new SeeingResolver(TextSurface, MenuBarIsland), clipboard, new CountingPaste())
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.FocusMoved, result.Outcome);
        Assert.Equal("the user's own clipboard", clipboard.Held);
    }

    [Fact]
    public async Task The_refusal_names_both_windows()
    {
        // "Insertion refused" with no subject is the log line this whole
        // investigation was fighting. The reason has to carry where it was
        // aimed and where focus actually went.
        var result = await Insertion(
            new SeeingResolver(TextSurface, MenuBarIsland), new Clipboard(), new CountingPaste())
            .InsertAsync("the spoken sentence");

        Assert.Contains("RichEditD2DPT", result.Reason);
        Assert.Contains("InputSiteWindowClass", result.Reason);
    }

    // ─── The cases that must NOT refuse ─────────────────────────────

    [Fact]
    public async Task A_focus_target_that_holds_still_is_typed_into()
    {
        var paste = new CountingPaste();
        var result = await Insertion(
            new SeeingResolver(TextSurface, TextSurface), new Clipboard(), paste)
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal(1, paste.Sends);
    }

    [Fact]
    public async Task A_target_with_no_caret_of_its_own_is_still_typed_into()
    {
        // THE GUARD ON THE MOST PLAUSIBLE WRONG FIX. Once you know the paste
        // was eaten by Notepad's menu-bar island, the obvious next move is to
        // refuse when the focus target has no caret, because Notepad measures
        // like this on the device (2026-08-20):
        //
        //   text surface focused   flags=0x1  hwndCaret=0x505B8
        //   menu bar armed         flags=0x0  hwndCaret=0x0      <- eaten
        //
        // It is wrong. Windows Terminal measures flags=0x0 hwndCaret=0x0 in
        // the same run and has never once lost a paste — it simply does not
        // use a Win32 caret. A caret check would refuse the application that
        // never had the bug, on every single dictation.
        //
        // So the caret separates Notepad-armed from Notepad-working, not
        // "will take text" from "will eat it". If someone wires it in anyway,
        // this test is what tells them, instead of a user discovering that
        // dictation stopped working in their terminal.
        var paste = new CountingPaste();
        var result = await Insertion(
            new SeeingResolver(CaretlessButWorking, CaretlessButWorking),
            new Clipboard(),
            paste)
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal(1, paste.Sends);
    }

    [Fact]
    public async Task A_resolver_that_cannot_be_asked_about_focus_types_exactly_as_before()
    {
        // The probe is discovered, not injected, and therefore optional. A
        // host that has not wired one must keep every outcome it had — a
        // check that fails closed here would remove dictation rather than
        // degrade it.
        var paste = new CountingPaste();
        var result = await Insertion(new BlindResolver(), new Clipboard(), paste)
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal(1, paste.Sends);
    }

    [Fact]
    public async Task An_unreadable_focus_target_is_not_evidence_of_a_move()
    {
        // Unknown means "this process could not find out", and a failed read
        // must never be the reason a user's sentence is dropped.
        var paste = new CountingPaste();
        var result = await Insertion(
            new SeeingResolver(KeyboardFocusTarget.Unknown, KeyboardFocusTarget.Unknown),
            new Clipboard(),
            paste)
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal(1, paste.Sends);
    }

    [Fact]
    public async Task One_readable_target_and_one_unreadable_one_is_not_a_move_either()
    {
        var paste = new CountingPaste();
        var result = await Insertion(
            new SeeingResolver(TextSurface, KeyboardFocusTarget.Unknown),
            new Clipboard(),
            paste)
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal(1, paste.Sends);
    }

    [Fact]
    public async Task A_probe_that_throws_does_not_lose_the_sentence()
    {
        var paste = new CountingPaste();
        var resolver = new SeeingResolver(TextSurface) { Throws = true };

        var result = await Insertion(resolver, new Clipboard(), paste)
            .InsertAsync("the spoken sentence");

        Assert.Equal(FocusedInsertionOutcome.Success, result.Outcome);
        Assert.Equal(1, paste.Sends);
    }

    // ─── The log line ───────────────────────────────────────────────

    [Fact]
    public async Task The_delivered_line_names_the_window_the_keystroke_went_to()
    {
        // THE POINT OF THE WHOLE CHANGE. "Inserted 358 characters into
        // Notepad" over an empty document cost this investigation its first
        // hour, because the line asserted a landing and named nothing that
        // could be checked. The class of the focused window is the field that
        // separates Notepad's document from Notepad's menu bar.
        var logger = new RecordingLogger();
        await Insertion(
            new SeeingResolver(TextSurface, TextSurface),
            new Clipboard(),
            new CountingPaste(),
            logger)
            .InsertAsync("the spoken sentence");

        var line = Assert.Single(logger.Lines, l => l.Contains("19 characters"));
        Assert.Contains("RichEditD2DPT", line);
        Assert.Contains("notepad", line);
    }

    [Fact]
    public async Task The_delivered_line_no_longer_claims_the_text_was_inserted()
    {
        // It claims the typing, which is what this class observed. "Inserted"
        // is a claim about the user's document that nothing here can make.
        var logger = new RecordingLogger();
        await Insertion(
            new SeeingResolver(TextSurface, TextSurface),
            new Clipboard(),
            new CountingPaste(),
            logger)
            .InsertAsync("the spoken sentence");

        Assert.DoesNotContain(logger.Lines, l => l.StartsWith("Inserted ", StringComparison.Ordinal));
    }

    // ─── The wiring ─────────────────────────────────────────────────

    [Fact]
    public async Task The_focus_is_read_twice_once_when_aiming_and_once_before_firing()
    {
        // A single read would make the comparison a tautology and the whole
        // check dead code that still logs a window name — the exact shape of
        // "the feature is wired and does nothing" this programme keeps
        // shipping.
        var resolver = new SeeingResolver(TextSurface, TextSurface);

        await Insertion(resolver, new Clipboard(), new CountingPaste())
            .InsertAsync("the spoken sentence");

        Assert.Equal(2, resolver.Reads);
    }

    [Fact]
    public void The_shipped_Windows_resolver_is_one_that_can_be_asked_about_focus()
    {
        // The seam is discovered at run time, so nothing in the type system
        // says the real resolver implements it — and a probe nobody
        // implements is a check that silently never runs while the log
        // cheerfully prints "unknown" forever. Asserted by reflection, not by
        // construction: this project is net9.0 and must never CALL a
        // P/Invoke, only look at one.
        var resolver = typeof(DictationAssemblyMarker).Assembly
            .GetType("Dictation.Services.Win32FocusedApplicationResolver");

        Assert.NotNull(resolver);
        Assert.True(
            typeof(IKeyboardFocusProbe).IsAssignableFrom(resolver),
            "Win32FocusedApplicationResolver no longer implements IKeyboardFocusProbe, "
            + "so the inserter cannot see which window its keystroke goes to and "
            + "every insertion logs an unknown focus target");
    }

    // ─── The value type ─────────────────────────────────────────────

    [Fact]
    public void An_unknown_target_differs_from_nothing_including_itself()
    {
        Assert.False(KeyboardFocusTarget.Unknown.DiffersFrom(KeyboardFocusTarget.Unknown));
        Assert.False(KeyboardFocusTarget.Unknown.DiffersFrom(TextSurface));
        Assert.False(TextSurface.DiffersFrom(KeyboardFocusTarget.Unknown));
    }

    [Fact]
    public void Two_read_targets_differ_exactly_when_their_handles_do()
    {
        Assert.True(TextSurface.DiffersFrom(MenuBarIsland));
        Assert.False(TextSurface.DiffersFrom(KeyboardFocusTarget.Of(0x505b8, "RichEditD2DPT")));
        // The class name is for humans; the handle is the identity. A window
        // that kept its handle and changed its class is the same window.
        Assert.False(TextSurface.DiffersFrom(KeyboardFocusTarget.Of(0x505b8, "SomethingElse")));
    }

    [Fact]
    public void A_successful_read_of_nothing_is_not_an_unknown()
    {
        // "The desktop has focus" is an answer. "I could not ask" is not, and
        // the two must not collapse — one of them is evidence.
        var nothing = KeyboardFocusTarget.Of(0, null);

        Assert.True(nothing.WasRead);
        Assert.Equal("nothing", nothing.ToString());
        Assert.Equal("unknown", KeyboardFocusTarget.Unknown.ToString());
        Assert.True(nothing.DiffersFrom(TextSurface));
    }

    [Fact]
    public void A_target_reads_as_its_class_and_its_handle()
    {
        Assert.Equal("RichEditD2DPT #0x505b8", TextSurface.ToString());
    }
}
