using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

public sealed class DictationCopyTests
{
    private static readonly HotkeyBinding Key = new(0, DictationHotkeys.VkRightControl, "Hold Right Ctrl");

    public static TheoryData<DictationOutcome> Outcomes()
    {
        var data = new TheoryData<DictationOutcome>();
        foreach (var outcome in Enum.GetValues<DictationOutcome>())
        {
            data.Add(outcome);
        }

        data.Add((DictationOutcome)99);
        return data;
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void No_outcome_puts_an_identifier_on_the_status_line(DictationOutcome outcome)
    {
        // The window used to print "Nothing inserted: MicrophoneBlocked. You
        // can try again." Every outcome now has a sentence; a member added
        // later lands on the generic one, never on its own name.
        var text = DictationCopy.ForResult(
            new DictationSessionResult(outcome, null, null), Key, () => MicrophoneAccess.Denied).Text;

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain(((int)outcome).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".", text);
        var name = outcome.ToString();
        if (name.Count(char.IsUpper) > 1 || char.IsDigit(name[0]))
        {
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_blocked_microphone_routes_to_the_privacy_page()
    {
        var status = DictationCopy.ForResult(
            new DictationSessionResult(DictationOutcome.MicrophoneBlocked, null, null), Key,
            () => MicrophoneAccess.Denied);

        Assert.True(status.OffersMicrophoneSettings);
        Assert.Contains("Privacy & security", status.Text, StringComparison.Ordinal);
        Assert.Equal("ms-settings:privacy-microphone", DictationCopy.MicrophoneSettingsUri);
    }

    [Fact]
    public void A_policy_block_does_not_send_the_user_to_a_greyed_out_switch()
    {
        var status = DictationCopy.ForResult(
            new DictationSessionResult(DictationOutcome.MicrophoneBlocked, null, null), Key,
            () => MicrophoneAccess.DeniedByPolicy);

        Assert.False(status.OffersMicrophoneSettings);
        Assert.Contains("administrator", status.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DictationOutcome.Inserted)]
    [InlineData(DictationOutcome.ClipboardRestoreFailed)]
    public void A_delivered_dictation_says_it_was_inserted(DictationOutcome outcome)
    {
        var text = DictationCopy.ForResult(new DictationSessionResult(outcome, "hi", null), Key).Text;

        Assert.StartsWith("Text inserted", text, StringComparison.Ordinal);
    }
}
