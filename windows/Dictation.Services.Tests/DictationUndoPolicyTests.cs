using Dictation.Services;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// The gate that decides whether Dictation may press backspace.
/// </summary>
/// <remarks>
/// <para>
/// Ported from macOS's <c>runDictationVoiceDeleteSelfCheck</c>, and the reason
/// the gate is a pure function is the reason this file can exist at all: the
/// branches that matter most are the ones nobody can stage by hand — a
/// two-minute wait, a second window, a password field. Every one of them is a
/// call with different arguments here.
/// </para>
/// <para>
/// <b>The ordering assertions are not tidiness.</b> A record can be expired
/// AND in a password field at once, and which refusal comes back decides
/// whether the secure-field gate is reachable at all. macOS shipped that gate
/// missing for one review cycle; these tests are what would say so.
/// </para>
/// </remarks>
public sealed class DictationUndoPolicyTests
{
    // A fixed clock. Decide reads no clock of its own, which is what makes
    // every age case below a plain argument rather than a wait.
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    // The window the insertion went into: a handle and the class name Win32
    // reports for it. Both halves are the identity — see the DifferentWindow
    // block.
    private static readonly nint Window = 0x00A1_B2C3;
    private static readonly nint OtherWindow = 0x00D4_E5F6;
    private const string WindowClass = "Notepad";
    private const string OtherWindowClass = "Chrome_WidgetWin_1";

    private static DictationInsertionRecord Record(
        string text = "hello",
        nint? window = null,
        string? windowClass = WindowClass,
        double ageSeconds = 1) =>
        new(text, window ?? Window, windowClass, Now.AddSeconds(-ageSeconds));

    private static DictationUndoDecision Decide(
        DictationInsertionRecord? record,
        nint? currentWindow = null,
        string? currentWindowClass = WindowClass,
        bool secureFieldFocused = false) =>
        DictationUndoPolicy.Decide(
            record,
            currentWindow ?? Window,
            currentWindowClass,
            secureFieldFocused,
            Now);

    // ── The one case that presses a key ──────────────────────────────────────

    [Fact]
    public void Same_window_fresh_and_sane_deletes()
    {
        var decision = Decide(Record("hello"));

        Assert.True(decision.Allowed);
        Assert.Equal(DictationUndoRefusal.None, decision.Refusal);
        Assert.Equal(5, decision.BackspaceCount);
    }

    [Fact]
    public void The_count_is_the_grapheme_count_of_the_recorded_text()
    {
        // The decision carries the number so there is exactly one place in the
        // product that decides how many keystrokes leave the app. If the policy
        // recounted with .Length here, the pin in DictationDeleteCommandTests
        // would still pass and the user would still lose four characters.
        var decision = Decide(Record("你好世界"));

        Assert.True(decision.Allowed);
        Assert.Equal(4, decision.BackspaceCount);
    }

    // ── 1. Nothing recorded ──────────────────────────────────────────────────

    [Fact]
    public void No_record_is_nothing_recorded()
    {
        var decision = Decide(null);

        Assert.False(decision.Allowed);
        Assert.Equal(DictationUndoRefusal.NothingRecorded, decision.Refusal);
        Assert.Equal(0, decision.BackspaceCount);
    }

    [Fact]
    public void With_no_record_the_answer_is_nothing_recorded_even_in_a_password_field()
    {
        // Ordering, first step. With nothing to delete the honest answer is
        // "there is nothing to remove", not "wrong field" — the field is not
        // the user's problem when the record is empty, and a HUD that blamed
        // it would send them looking in the wrong place.
        var decision = Decide(null, secureFieldFocused: true);

        Assert.Equal(DictationUndoRefusal.NothingRecorded, decision.Refusal);
    }

    // ── 2. The password field, and it comes before everything else ───────────

    [Fact]
    public void A_password_field_refuses_an_otherwise_perfect_undo()
    {
        // THE case that shipped broken on macOS for one review cycle: dictate
        // into a normal field, click a password field IN THE SAME APP, say
        // "scratch that". Same window, one second old, five characters —
        // everything else about this undo is valid.
        var decision = Decide(Record("hello"), secureFieldFocused: true);

        Assert.False(decision.Allowed);
        Assert.Equal(DictationUndoRefusal.SecureField, decision.Refusal);
        Assert.Equal(0, decision.BackspaceCount);
    }

    [Fact]
    public void An_expired_record_in_a_password_field_reports_the_password_field()
    {
        // The load-bearing ordering assertion. Both refusals apply; reporting
        // Expired here would mean the secure-field branch is unreachable
        // whenever the age check happens to fire first, which is exactly how a
        // safety gate rots into decoration.
        var decision = Decide(
            Record(ageSeconds: DictationUndoPolicy.MaxAge.TotalSeconds + 60),
            secureFieldFocused: true);

        Assert.Equal(DictationUndoRefusal.SecureField, decision.Refusal);
    }

    [Fact]
    public void A_different_window_in_a_password_field_reports_the_password_field()
    {
        var decision = Decide(
            Record(),
            currentWindow: OtherWindow,
            currentWindowClass: OtherWindowClass,
            secureFieldFocused: true);

        Assert.Equal(DictationUndoRefusal.SecureField, decision.Refusal);
    }

    [Fact]
    public void An_absurd_record_in_a_password_field_reports_the_password_field()
    {
        var decision = Decide(
            Record(new string('x', DictationDeleteCommand.MaxBackspaces + 1)),
            secureFieldFocused: true);

        Assert.Equal(DictationUndoRefusal.SecureField, decision.Refusal);
    }

    // ── 3. Age ───────────────────────────────────────────────────────────────

    [Fact]
    public void Two_minutes_is_the_bound()
    {
        // Pinned at the number macOS ships. Long enough to read what was
        // typed, decide it is wrong and say so; short enough that a record
        // left over from before lunch cannot eat a paragraph the user has
        // since written by hand into the same field.
        Assert.Equal(TimeSpan.FromSeconds(120), DictationUndoPolicy.MaxAge);
    }

    [Fact]
    public void Exactly_at_the_bound_is_still_undoable()
    {
        var decision = Decide(Record(ageSeconds: DictationUndoPolicy.MaxAge.TotalSeconds));

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void One_second_past_the_bound_is_expired()
    {
        var decision = Decide(Record(ageSeconds: DictationUndoPolicy.MaxAge.TotalSeconds + 1));

        Assert.False(decision.Allowed);
        Assert.Equal(DictationUndoRefusal.Expired, decision.Refusal);
        Assert.Equal(0, decision.BackspaceCount);
    }

    [Fact]
    public void A_record_dated_in_the_future_is_expired()
    {
        // A clock that went backwards — an NTP step, a sleep/wake resume — is
        // not a licence. Without this branch a negative age passes "age >
        // MaxAge" forever, so a record could stay undoable for as long as the
        // clock stayed behind.
        var decision = Decide(Record(ageSeconds: -5));

        Assert.Equal(DictationUndoRefusal.Expired, decision.Refusal);
    }

    [Fact]
    public void An_expired_record_in_a_different_window_reports_expired()
    {
        // Ordering, third step: age is checked before window identity.
        var decision = Decide(
            Record(ageSeconds: DictationUndoPolicy.MaxAge.TotalSeconds + 60),
            currentWindow: OtherWindow,
            currentWindowClass: OtherWindowClass);

        Assert.Equal(DictationUndoRefusal.Expired, decision.Refusal);
    }

    // ── 4. Window identity — handle AND class, unknown is a refusal ──────────

    [Fact]
    public void Another_window_refuses()
    {
        var decision = Decide(
            Record(),
            currentWindow: OtherWindow,
            currentWindowClass: OtherWindowClass);

        Assert.False(decision.Allowed);
        Assert.Equal(DictationUndoRefusal.DifferentWindow, decision.Refusal);
        Assert.Equal(0, decision.BackspaceCount);
    }

    [Fact]
    public void The_same_handle_with_a_different_class_refuses()
    {
        // Finer than macOS on purpose. HWNDs are recycled: a window closed and
        // another opened inside the two-minute window can be handed the same
        // numeric handle, and the class is the cheapest thing that disagrees
        // when that happens.
        var decision = Decide(Record(), currentWindowClass: OtherWindowClass);

        Assert.Equal(DictationUndoRefusal.DifferentWindow, decision.Refusal);
    }

    [Fact]
    public void A_different_handle_with_the_same_class_refuses()
    {
        // Two Notepad windows. This is the case macOS cannot refuse at all —
        // its guard compares bundle ids, so a different ordinary field in the
        // same application is undo-eligible there (its own KNOWN_ISSUES K-1).
        // Windows can close it, and this is the assertion that says it did.
        var decision = Decide(Record(), currentWindow: OtherWindow);

        Assert.Equal(DictationUndoRefusal.DifferentWindow, decision.Refusal);
    }

    [Theory]
    // An unknown value on EITHER side is a refusal, never a pass. "We could
    // not tell which window this is" must never resolve to "so go ahead and
    // delete in it" — that reading turns every failed GetClassName, and every
    // moment with no foreground window, into a licence.
    [InlineData(true, false, WindowClass, WindowClass)]      // recorded handle unknown
    [InlineData(false, true, WindowClass, WindowClass)]      // current handle unknown
    [InlineData(false, false, null, WindowClass)]            // recorded class unknown
    [InlineData(false, false, WindowClass, null)]            // current class unknown
    [InlineData(false, false, "", WindowClass)]              // empty is unknown too
    [InlineData(false, false, WindowClass, "   ")]           // and so is whitespace
    public void An_unidentifiable_window_on_either_side_refuses(
        bool recordedHandleUnknown,
        bool currentHandleUnknown,
        string? recordedClass,
        string? currentClass)
    {
        var decision = DictationUndoPolicy.Decide(
            Record(window: recordedHandleUnknown ? 0 : Window, windowClass: recordedClass),
            currentHandleUnknown ? 0 : Window,
            currentClass,
            secureFieldFocused: false,
            Now);

        Assert.False(decision.Allowed);
        Assert.Equal(DictationUndoRefusal.DifferentWindow, decision.Refusal);
    }

    [Fact]
    public void A_different_window_holding_an_absurd_record_reports_the_window()
    {
        // Ordering, fourth step: identity is checked before length.
        var decision = Decide(
            Record(new string('x', DictationDeleteCommand.MaxBackspaces + 1)),
            currentWindow: OtherWindow,
            currentWindowClass: OtherWindowClass);

        Assert.Equal(DictationUndoRefusal.DifferentWindow, decision.Refusal);
    }

    // ── 5. Length ────────────────────────────────────────────────────────────

    [Fact]
    public void A_record_at_the_ceiling_is_still_undoable()
    {
        var decision = Decide(Record(new string('x', DictationDeleteCommand.MaxBackspaces)));

        Assert.True(decision.Allowed);
        Assert.Equal(DictationDeleteCommand.MaxBackspaces, decision.BackspaceCount);
    }

    [Fact]
    public void One_past_the_ceiling_is_too_long()
    {
        var decision = Decide(Record(new string('x', DictationDeleteCommand.MaxBackspaces + 1)));

        Assert.False(decision.Allowed);
        Assert.Equal(DictationUndoRefusal.TooLong, decision.Refusal);
        Assert.Equal(0, decision.BackspaceCount);
    }

    [Fact]
    public void An_empty_record_is_too_long_not_a_clean_no_op()
    {
        // Empty is TooLong's sibling rather than an allowed zero-keystroke
        // success. A zero-length record means the bookkeeping is wrong, and
        // "succeeded by sending nothing" would report a clean delete for text
        // that is still on the screen.
        var decision = Decide(Record(string.Empty));

        Assert.False(decision.Allowed);
        Assert.Equal(DictationUndoRefusal.TooLong, decision.Refusal);
    }

    [Fact]
    public void A_record_of_only_a_ceiling_worth_of_clusters_is_measured_in_clusters()
    {
        // MaxBackspaces is a bound on KEYSTROKES, so it must be applied to the
        // cluster count and not to the string's length. 1200 smileys are 2400
        // UTF-16 units — over the bound by that measure — and 1200 keystrokes,
        // which is under it.
        var decision = Decide(Record(string.Concat(Enumerable.Repeat("\U0001F642", 1200))));

        Assert.True(decision.Allowed);
        Assert.Equal(1200, decision.BackspaceCount);
    }

    // ── The decision type itself ─────────────────────────────────────────────

    [Fact]
    public void A_defaulted_decision_sends_no_keystrokes()
    {
        // None is zero so a defaulted struct reads as "allowed" — which is
        // only safe because the count travels with it. Pinned here so that
        // stays true if the struct grows a field.
        var decision = default(DictationUndoDecision);

        Assert.True(decision.Allowed);
        Assert.Equal(0, decision.BackspaceCount);
    }

    [Fact]
    public void Allowed_is_exactly_the_absence_of_a_refusal()
    {
        Assert.True(new DictationUndoDecision(DictationUndoRefusal.None, 5).Allowed);

        foreach (var refusal in Enum.GetValues<DictationUndoRefusal>())
        {
            if (refusal == DictationUndoRefusal.None)
            {
                continue;
            }

            Assert.False(new DictationUndoDecision(refusal, 5).Allowed);
        }
    }

    [Fact]
    public void Decide_is_total_over_the_refusals_it_declares()
    {
        // Every value in the enum is reachable from Decide. A refusal nothing
        // returns is a branch somebody deleted, and the enum is the only place
        // that would still mention it.
        var reached = new HashSet<DictationUndoRefusal>
        {
            Decide(null).Refusal,
            Decide(Record(), secureFieldFocused: true).Refusal,
            Decide(Record(ageSeconds: 10_000)).Refusal,
            Decide(Record(), currentWindow: OtherWindow).Refusal,
            Decide(Record(string.Empty)).Refusal,
            Decide(Record()).Refusal,
        };

        foreach (var refusal in Enum.GetValues<DictationUndoRefusal>())
        {
            Assert.Contains(refusal, reached);
        }
    }
}
