using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The worker's transcribe reply grows additive fields: an empty decode now
/// also carries <c>dropped</c>, the one word the Mac's ending reads. This
/// client reads the fields it knows and ignores the rest.
/// </summary>
public sealed class AsrResponseWireTests
{
    [Fact]
    public async Task A_reply_carrying_the_drop_word_still_reads()
    {
        using var stream = new MemoryStream();
        await AsrFraming.WriteFrameAsync(
            stream,
            new { ok = true, text = "", dropped_reason = "junk-filter", dropped = "junk" },
            CancellationToken.None);
        stream.Position = 0;

        var reply = await AsrFraming.ReadFrameAsync<AsrResponse>(stream, CancellationToken.None);

        Assert.NotNull(reply);
        Assert.True(reply.Ok);
        Assert.Equal(string.Empty, reply.Text);
        Assert.Equal("junk-filter", reply.DroppedReason);
    }
}
