using Dictation.Services;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// What Dictation says when Windows is the thing refusing the microphone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap.</b> Every microphone problem collapsed into one pill — "The
/// microphone would not open" — which is indistinguishable from a broken
/// feature. Windows 11 ships a per-app microphone switch under Privacy &amp;
/// security; with it off, dictation failed with no hint that a permission
/// existed, let alone where. On a managed machine that is the likeliest
/// first-run failure there is.
/// </para>
/// <para>
/// macOS has enumerated these as named states with their own copy from the
/// beginning, including <c>microphoneBusy(owner:)</c>, whose text says out loud
/// "This is normal, not a fault."
/// </para>
/// </remarks>
public sealed class MicrophoneAccessTests
{
    [Fact]
    public void Unknown_is_zero_so_a_default_never_accuses_anybody()
    {
        // A probe that could not read the consent store, a build with no probe
        // wired, a defaulted field — all the same value, and that value must
        // mean "carry on".
        Assert.Equal(MicrophoneAccess.Unknown, default(MicrophoneAccess));
        Assert.Null(MicrophoneAccessCopy.Sentence(default));
        Assert.False(MicrophoneAccessCopy.IsBlocked(default));
        Assert.False(MicrophoneAccessCopy.OffersSettingsLink(default));
    }

    [Fact]
    public void An_ordinary_launch_gains_no_extra_line()
    {
        // Allowed says nothing. A card announcing "the microphone is fine" on
        // every launch is one more line to read past on the busiest surface in
        // the app.
        Assert.Null(MicrophoneAccessCopy.Sentence(MicrophoneAccess.Allowed));
        Assert.False(MicrophoneAccessCopy.IsBlocked(MicrophoneAccess.Allowed));
    }

    [Fact]
    public void A_denial_names_the_setting_and_offers_the_way_there()
    {
        var sentence = MicrophoneAccessCopy.Sentence(MicrophoneAccess.Denied);

        Assert.NotNull(sentence);
        // The page by name, so a user whose Settings app will not launch still
        // knows where to go without the button.
        Assert.Contains("Privacy & security", sentence, StringComparison.Ordinal);
        Assert.True(MicrophoneAccessCopy.IsBlocked(MicrophoneAccess.Denied));
        Assert.True(MicrophoneAccessCopy.OffersSettingsLink(MicrophoneAccess.Denied));
    }

    [Fact]
    public void The_deep_link_is_the_microphone_privacy_page()
    {
        // Not a generic ms-settings: root. Landing a user on the Settings home
        // page and asking them to find a per-app microphone switch is barely
        // better than not linking at all.
        Assert.Equal("ms-settings:privacy-microphone", MicrophoneAccessCopy.PrivacySettingsUri);
    }

    [Fact]
    public void A_policy_denial_says_so_and_offers_no_button()
    {
        // The Settings toggle is greyed out under a policy. A button carrying
        // the user to a control they cannot move reads as the app being wrong
        // about its own diagnosis.
        var sentence = MicrophoneAccessCopy.Sentence(MicrophoneAccess.DeniedByPolicy);

        Assert.NotNull(sentence);
        Assert.Contains("administrator", sentence, StringComparison.Ordinal);
        Assert.True(MicrophoneAccessCopy.IsBlocked(MicrophoneAccess.DeniedByPolicy));
        Assert.False(MicrophoneAccessCopy.OffersSettingsLink(MicrophoneAccess.DeniedByPolicy));
    }

    [Fact]
    public void Busy_is_explicitly_not_a_fault()
    {
        // macOS's own reassurance, carried over. The state is otherwise read as
        // a failure, and the user's next move after "not a fault" is to wait
        // rather than to file a bug.
        var sentence = MicrophoneAccessCopy.Sentence(MicrophoneAccess.Busy);

        Assert.NotNull(sentence);
        Assert.Contains("not a fault", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Busy_does_not_block_a_hold()
    {
        // Windows shares capture endpoints, so another application holding one
        // usually does not stop this one opening it. The state is worth NAMING
        // when a hold fails and worth not pre-emptively refusing on.
        Assert.False(MicrophoneAccessCopy.IsBlocked(MicrophoneAccess.Busy));
        Assert.False(MicrophoneAccessCopy.OffersSettingsLink(MicrophoneAccess.Busy));
    }

    [Theory]
    [InlineData(MicrophoneAccess.Denied)]
    [InlineData(MicrophoneAccess.DeniedByPolicy)]
    [InlineData(MicrophoneAccess.Busy)]
    public void Every_state_worth_naming_has_a_sentence(MicrophoneAccess access)
    {
        // A state with no copy is a state the user experiences as silence,
        // which is the whole defect this closes.
        var sentence = MicrophoneAccessCopy.Sentence(access);

        Assert.False(string.IsNullOrWhiteSpace(sentence));
    }

    [Fact]
    public void A_button_is_only_ever_offered_beside_a_sentence()
    {
        // The disabled-control-and-its-reason rule this pane already keeps,
        // pointed the other way: an action with no explanation beside it is a
        // button the user has to guess at.
        foreach (var access in Enum.GetValues<MicrophoneAccess>())
        {
            if (MicrophoneAccessCopy.OffersSettingsLink(access))
            {
                Assert.False(string.IsNullOrWhiteSpace(MicrophoneAccessCopy.Sentence(access)));
            }
        }
    }
    // ----- "Check again": the half of the fix that closes the loop -----
    //
    // Re-reading on window activation only helps a user who LEAVES and returns
    // to this window. Someone who flips the switch on a second monitor and
    // looks back has never left, and the accusation is still there.

    [Fact]
    public void Check_again_is_offered_wherever_there_is_a_sentence()
    {
        // Wider than the deep link on purpose: re-reading claims nothing about
        // who can fix the problem.
        Assert.True(MicrophoneAccessCopy.OffersRecheck(MicrophoneAccess.Denied));
        Assert.True(MicrophoneAccessCopy.OffersRecheck(MicrophoneAccess.DeniedByPolicy));
        Assert.True(MicrophoneAccessCopy.OffersRecheck(MicrophoneAccess.Busy));
    }

    [Fact]
    public void A_policy_denial_offers_no_deep_link_but_does_offer_a_re_read()
    {
        // The reason the button is withheld — a greyed-out toggle the user
        // cannot move — says nothing about reading the value again. An
        // administrator may have lifted the policy, and the user has no other
        // way to find out.
        Assert.False(MicrophoneAccessCopy.OffersSettingsLink(MicrophoneAccess.DeniedByPolicy));
        Assert.True(MicrophoneAccessCopy.OffersRecheck(MicrophoneAccess.DeniedByPolicy));
    }

    [Fact]
    public void Busy_is_the_state_a_re_read_helps_most()
    {
        // The other application letting go is the likeliest next event and is
        // invisible until something asks again. It is also the one state with
        // no deep link and no fault to report.
        Assert.True(MicrophoneAccessCopy.OffersRecheck(MicrophoneAccess.Busy));
        Assert.False(MicrophoneAccessCopy.OffersSettingsLink(MicrophoneAccess.Busy));
        Assert.False(MicrophoneAccessCopy.IsBlocked(MicrophoneAccess.Busy));
    }

    [Fact]
    public void A_quiet_microphone_offers_nothing_to_re_read()
    {
        // Same rule as the sentence itself: no accusation, no controls.
        Assert.False(MicrophoneAccessCopy.OffersRecheck(MicrophoneAccess.Allowed));
        Assert.False(MicrophoneAccessCopy.OffersRecheck(MicrophoneAccess.Unknown));
        Assert.Null(MicrophoneAccessCopy.Unchanged(MicrophoneAccess.Allowed));
        Assert.Null(MicrophoneAccessCopy.Unchanged(MicrophoneAccess.Unknown));
    }

    [Theory]
    [InlineData(MicrophoneAccess.Denied)]
    [InlineData(MicrophoneAccess.DeniedByPolicy)]
    [InlineData(MicrophoneAccess.Busy)]
    public void Every_re_readable_state_can_report_that_nothing_moved(MicrophoneAccess access)
    {
        // A button that silently does nothing is worse than no button: the user
        // cannot tell a working re-read from a dead control. So the two sets
        // must match exactly — an offered re-read with no line to print would
        // be that dead control.
        Assert.True(MicrophoneAccessCopy.OffersRecheck(access));
        var line = MicrophoneAccessCopy.Unchanged(access);
        Assert.False(string.IsNullOrWhiteSpace(line));
        Assert.StartsWith("Still", line);
    }

    [Fact]
    public void The_unchanged_line_never_calls_a_busy_microphone_blocked()
    {
        // Busy is not a fault, which its own sentence says out loud and
        // IsBlocked agrees with. The follow-up must not quietly reverse that.
        var line = MicrophoneAccessCopy.Unchanged(MicrophoneAccess.Busy);
        Assert.NotNull(line);
        Assert.DoesNotContain("blocked", line!, System.StringComparison.OrdinalIgnoreCase);
        Assert.False(MicrophoneAccessCopy.IsBlocked(MicrophoneAccess.Busy));
    }

    [Fact]
    public void A_denial_that_has_not_moved_names_the_switch_people_miss()
    {
        // Dictation's own switch is useless while the global "desktop apps" one
        // above it is off. Someone who turned on the first and came back to an
        // unchanged verdict needs to be told about the second, or the re-read
        // reads as broken.
        var line = MicrophoneAccessCopy.Unchanged(MicrophoneAccess.Denied);
        Assert.NotNull(line);
        Assert.Contains("desktop apps", line!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_policy_that_has_not_moved_says_who_can_move_it()
    {
        var line = MicrophoneAccessCopy.Unchanged(MicrophoneAccess.DeniedByPolicy);
        Assert.NotNull(line);
        Assert.Contains("administrator", line!, System.StringComparison.OrdinalIgnoreCase);
    }

}
