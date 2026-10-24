using Dictation.Services;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// The accumulation contract for the live dictation preview.
/// </summary>
/// <remarks>
/// The first test is the one that matters: it is the measured 80%-WER defect
/// expressed as an assertion, so a future refactor that drops
/// <c>isEndpoint</c> — which is exactly what the wire layer did before this
/// feature — fails here instead of silently shipping a preview that loses
/// every sentence but the last.
/// </remarks>
public sealed class DictationPreviewAccumulatorTests
{
    [Fact]
    public void Text_survives_an_endpoint_because_the_worker_has_already_reset_its_stream()
    {
        var acc = new DictationPreviewAccumulator();

        acc.Observe("hello there", isEndpoint: false);
        acc.Observe("hello there world", isEndpoint: true);
        // Post-reset the worker answers for a FRESH segment. A caller that
        // kept only this reply would show "and then some" and nothing else.
        var text = acc.Observe("and then some", isEndpoint: false);

        Assert.Equal("hello there world and then some", text);
    }

    [Fact]
    public void A_hold_that_ends_in_silence_keeps_its_words()
    {
        // The exact shape that produced "whole clips coming back empty":
        // speech, then an endpoint carrying the text, then a trailing-silence
        // push that reports nothing.
        var acc = new DictationPreviewAccumulator();

        acc.Observe("the quick brown fox", isEndpoint: true);
        var text = acc.Observe("", isEndpoint: true);

        Assert.Equal("the quick brown fox", text);
    }

    [Fact]
    public void An_endpoint_with_no_text_appends_nothing()
    {
        var acc = new DictationPreviewAccumulator();

        acc.Observe("one", isEndpoint: true);
        acc.Observe("   ", isEndpoint: true);
        acc.Observe(null, isEndpoint: true);
        var text = acc.Observe("two", isEndpoint: true);

        // No double spaces, no empty entries between the two real segments.
        Assert.Equal("one two", text);
    }

    [Fact]
    public void Multiple_endpoints_accumulate_in_order()
    {
        var acc = new DictationPreviewAccumulator();

        acc.Observe("first", isEndpoint: true);
        acc.Observe("second", isEndpoint: true);
        var text = acc.Observe("third", isEndpoint: true);

        Assert.Equal("first second third", text);
    }

    [Fact]
    public void A_null_reply_leaves_the_live_tail_alone()
    {
        // A dropped or timed-out push answers null. Blanking the tail on that
        // would make text the user is mid-way through reading flicker out.
        var acc = new DictationPreviewAccumulator();

        acc.Observe("still talking", isEndpoint: false);
        var text = acc.Observe(null, isEndpoint: false);

        Assert.Equal("still talking", text);
    }

    [Fact]
    public void An_empty_reply_does_clear_the_live_tail()
    {
        // Distinct from null: the worker positively answered "nothing here",
        // which happens when its junk/hallucination filter empties a partial.
        var acc = new DictationPreviewAccumulator();

        acc.Observe("thank you", isEndpoint: false);
        var text = acc.Observe("", isEndpoint: false);

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void The_live_tail_is_replaced_not_appended()
    {
        // Within one segment the recogniser re-emits the whole segment each
        // push. Appending would render "I I am I am here".
        var acc = new DictationPreviewAccumulator();

        acc.Observe("I", isEndpoint: false);
        acc.Observe("I am", isEndpoint: false);
        var text = acc.Observe("I am here", isEndpoint: false);

        Assert.Equal("I am here", text);
    }

    [Fact]
    public void Committed_text_is_never_rewritten_by_a_later_partial()
    {
        // Append-only is the property that makes this safe to draw: the
        // preview may only grow while a hold runs.
        var acc = new DictationPreviewAccumulator();

        acc.Observe("locked in", isEndpoint: true);
        acc.Observe("x", isEndpoint: false);
        var text = acc.Observe("xy", isEndpoint: false);

        Assert.StartsWith("locked in", text);
        Assert.Equal("locked in xy", text);
    }

    [Fact]
    public void Reset_stops_one_hold_inheriting_the_previous_holds_words()
    {
        var acc = new DictationPreviewAccumulator();

        acc.Observe("previous hold", isEndpoint: true);
        acc.Reset();

        Assert.False(acc.HasText);
        Assert.Equal(string.Empty, acc.Text);
        Assert.Equal("new hold", acc.Observe("new hold", isEndpoint: false));
    }

    [Fact]
    public void A_fresh_accumulator_has_no_text()
    {
        var acc = new DictationPreviewAccumulator();

        Assert.False(acc.HasText);
        Assert.Equal(string.Empty, acc.Text);
    }

    [Fact]
    public void Whitespace_around_a_hypothesis_never_reaches_the_screen()
    {
        var acc = new DictationPreviewAccumulator();

        acc.Observe("  padded  ", isEndpoint: true);
        var text = acc.Observe("  tail  ", isEndpoint: false);

        Assert.Equal("padded tail", text);
    }
}
