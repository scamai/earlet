using Dictation.Services;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// The 2026-08-22 quieting rule: when the live caption already showed the user
/// their words arriving, the post-release pills that merely CONFIRM it are
/// suppressed. Failures are never suppressed.
/// </summary>
/// <remarks>
/// <para>
/// Two of these tests are the ones that matter, and both are exhaustive over
/// an enum rather than spot-checks — a new
/// <see cref="DictationOutcome"/> member added later must fail a test rather
/// than silently inherit a verdict:
/// </para>
/// <list type="bullet">
/// <item>the flag-off equivalence sweep, which is what "legacy is byte-for-byte
/// unchanged" actually means, proven rather than asserted; and</item>
/// <item>the classification sweep, which pins every outcome's verdict in one
/// readable table.</item>
/// </list>
/// </remarks>
public sealed class DictationHudQuietPreviewTests
{
    private static DictationSessionResult Result(DictationOutcome outcome) =>
        new(outcome, Text: "some words", Target: null);

    private static IEnumerable<DictationState> AllStates() =>
        Enum.GetValues<DictationState>();

    private static IEnumerable<DictationOutcome> AllOutcomes() =>
        Enum.GetValues<DictationOutcome>();

    // ── flag off is the legacy path, proven over the whole enum ─────────────

    [Fact]
    public void With_no_preview_every_state_renders_exactly_as_before()
    {
        // The legacy one-argument overload must be indistinguishable from the
        // new one asked for the same thing. Anything else means turning the
        // feature flag OFF still changed what users see.
        foreach (var state in AllStates())
        {
            Assert.Equal(
                DictationHudModel.For(state, previewShowedWords: false),
                DictationHudModel.For(state));
        }
    }

    [Fact]
    public void With_no_preview_every_outcome_renders_exactly_as_before()
    {
        foreach (var outcome in AllOutcomes())
        {
            var result = Result(outcome);

            Assert.Equal(
                DictationHudModel.For(result, firstEverInsert: false, previewShowedWords: false),
                DictationHudModel.For(result));

            Assert.Equal(
                DictationHudModel.For(result, firstEverInsert: true, previewShowedWords: false),
                DictationHudModel.For(result, firstEverInsert: true));
        }
    }

    [Fact]
    public void With_no_preview_nothing_is_hidden_that_used_to_speak()
    {
        // The sharpest form of the regression: with the flag off, no outcome
        // may resolve to Hidden. A preview that could silence the legacy path
        // would be the exact failure DictationHudModel's own header is about —
        // silence covering a failure the user needs to see.
        foreach (var outcome in AllOutcomes())
        {
            var model = DictationHudModel.For(Result(outcome), firstEverInsert: false,
                previewShowedWords: false);

            Assert.NotEqual(DictationHudModel.Hidden, model);
        }
    }

    // ── with the preview, confirmations go quiet ────────────────────────────

    [Fact]
    public void The_working_and_typing_pills_are_suppressed()
    {
        Assert.Equal(DictationHudModel.Hidden,
            DictationHudModel.For(DictationState.Transcribing, previewShowedWords: true));
        Assert.Equal(DictationHudModel.Hidden,
            DictationHudModel.For(DictationState.Inserting, previewShowedWords: true));
    }

    [Fact]
    public void Listening_is_untouched_because_the_caption_lives_inside_it()
    {
        // The caption is drawn during Listening. Hiding that pill would take
        // the caption off screen with it — the opposite of the feature.
        Assert.NotEqual(DictationHudModel.Hidden,
            DictationHudModel.For(DictationState.Listening, previewShowedWords: true));
    }

    [Fact]
    public void A_successful_insert_says_nothing()
    {
        Assert.Equal(DictationHudModel.Hidden,
            DictationHudModel.For(Result(DictationOutcome.Inserted),
                firstEverInsert: false, previewShowedWords: true));
    }

    [Fact]
    public void Every_outcome_is_classified_deliberately()
    {
        // The whole table, in one place. Adding a DictationOutcome without
        // deciding its verdict fails HERE, which is the point.
        var quieted = new HashSet<DictationOutcome>
        {
            DictationOutcome.Inserted,
            DictationOutcome.Cancelled,
        };

        foreach (var outcome in AllOutcomes())
        {
            var model = DictationHudModel.For(Result(outcome),
                firstEverInsert: false, previewShowedWords: true);

            if (quieted.Contains(outcome))
            {
                Assert.Equal(DictationHudModel.Hidden, model);
            }
            else
            {
                Assert.NotEqual(DictationHudModel.Hidden, model);
            }
        }
    }

    // ── the cases the classification exists to get right ────────────────────

    [Fact]
    public void A_clipboard_that_did_not_come_back_still_speaks()
    {
        // The text LANDED, so by the letter of "don't confirm what they
        // watched happen" this is a success. It still shows, because the user
        // has silently lost whatever they had copied and this pill is the only
        // place they would ever find that out.
        Assert.NotEqual(DictationHudModel.Hidden,
            DictationHudModel.For(Result(DictationOutcome.ClipboardRestoreFailed),
                firstEverInsert: false, previewShowedWords: true));
    }

    [Theory]
    [InlineData(DictationOutcome.HoldTooShort)]
    [InlineData(DictationOutcome.NoAudio)]
    [InlineData(DictationOutcome.NoSpeech)]
    [InlineData(DictationOutcome.SecureField)]
    public void A_hold_that_produced_no_text_still_speaks(DictationOutcome outcome)
    {
        // Not errors exactly — but the user got nothing, and silence there is
        // indistinguishable from a feature that is broken. This is the tension
        // DictationHudModel's header documents as eleven distinct failures that
        // silence used to cover.
        Assert.NotEqual(DictationHudModel.Hidden,
            DictationHudModel.For(Result(outcome),
                firstEverInsert: false, previewShowedWords: true));
    }

    [Fact]
    public void The_first_ever_insert_hint_outranks_the_quieting()
    {
        // "It types anywhere your cursor is" is taught ONCE, on the first
        // successful dictation of a user's life. Quieting it would mean a user
        // whose very first hold happened to run the preview never learns the
        // feature works outside this app.
        var model = DictationHudModel.For(Result(DictationOutcome.Inserted),
            firstEverInsert: true, previewShowedWords: true);

        Assert.NotEqual(DictationHudModel.Hidden, model);
        Assert.Contains("cursor", model.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_preview_that_showed_nothing_leaves_the_confirmation_alone()
    {
        // The rule rests on "the user already watched the words arrive". If the
        // preview produced no words — model missing, recogniser unavailable, or
        // they simply said nothing — they watched nothing, and suppressing the
        // confirmation restores exactly the silence this HUD exists to prevent.
        // That is why the parameter is previewShowedWords and not
        // previewEnabled.
        Assert.NotEqual(DictationHudModel.Hidden,
            DictationHudModel.For(Result(DictationOutcome.Inserted),
                firstEverInsert: false, previewShowedWords: false));

        Assert.NotEqual(DictationHudModel.Hidden,
            DictationHudModel.For(DictationState.Transcribing, previewShowedWords: false));
    }
}
